﻿using ARMeilleure.Memory.Tracking;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace ARMeilleure.Memory
{
    /// <summary>
    /// Represents a CPU memory manager.
    /// </summary>
    public sealed class MemoryManager : IMemoryManager, IDisposable
    {
        public const int PageBits = 12;
        public const int PageSize = 1 << PageBits;
        public const int PageMask = PageSize - 1;

        private const int PteSize = 8;

        public int AddressSpaceBits { get; }

        private readonly ulong _addressSpaceSize;

        private readonly IMemoryBlock _backingMemory;
        private readonly IMemoryBlock _pageTable;

        public IntPtr PageTablePointer => _pageTable.Pointer;

        public ulong WriteTrackOffset => (ulong)_backingMemory.MirrorPointer - (ulong)_backingMemory.Pointer;

        public MemoryTracking Tracking { get; }

        /// <summary>
        /// Creates a new instance of the memory manager.
        /// </summary>
        /// <param name="allocator">Allocator used for internal allocations on the memory manager</param>
        /// <param name="backingMemory">Physical backing memory where virtual memory will be mapped to</param>
        /// <param name="addressSpaceSize">Size of the address space</param>
        public MemoryManager(IMemoryAllocator allocator, IMemoryBlock backingMemory, ulong addressSpaceSize)
        {
            ulong asSize = PageSize;

            int asBits = 12;

            while (asSize < addressSpaceSize)
            {
                asSize <<= 1;

                asBits++;
            }

            AddressSpaceBits = asBits;
            _addressSpaceSize = asSize;
            _backingMemory = backingMemory;
            _pageTable = allocator.Allocate((asSize / PageSize) * PteSize);

            Tracking = new MemoryTracking(backingMemory);
            Tracking.VirtualToPhysicalConverter = GetPhysicalRegions;
            Tracking.VirtualProtect = MarkRegionProtection;
        }

        /// <summary>
        /// Maps a virtual memory range into a physical memory range.
        /// </summary>
        /// <remarks>
        /// Addresses and size must be page aligned.
        /// </remarks>
        /// <param name="va">Virtual memory address</param>
        /// <param name="pa">Physical memory address</param>
        /// <param name="size">Size to be mapped</param>
        public void Map(ulong va, ulong pa, ulong size)
        {
            while (size != 0)
            {
                _pageTable.Write((va / PageSize) * PteSize, PaToPte(pa));

                va += PageSize;
                pa += PageSize;
                size -= PageSize;
            }
            Tracking.Map(va, pa, size);
        }

        /// <summary>
        /// Unmaps a previously mapped range of virtual memory.
        /// </summary>
        /// <param name="va">Virtual address of the range to be unmapped</param>
        /// <param name="size">Size of the range to be unmapped</param>
        public void Unmap(ulong va, ulong size)
        {
            while (size != 0)
            {
                _pageTable.Write((va / PageSize) * PteSize, 0UL);

                va += PageSize;
                size -= PageSize;
            }
            Tracking.Unmap(va, size);
        }

        /// <summary>
        /// Reads data from CPU mapped memory.
        /// </summary>
        /// <typeparam name="T">Type of the data being read</typeparam>
        /// <param name="va">Virtual address of the data in memory</param>
        /// <returns>The data</returns>
        public T Read<T>(ulong va) where T : unmanaged
        {
            return MemoryMarshal.Cast<byte, T>(GetSpan(va, Unsafe.SizeOf<T>()))[0];
        }

        /// <summary>
        /// Reads data from CPU mapped memory.
        /// </summary>
        /// <param name="va">Virtual address of the data in memory</param>
        /// <param name="data">Span to store the data being read into</param>
        public void Read(ulong va, Span<byte> data)
        {
            ReadImpl(va, data);
        }

        /// <summary>
        /// Writes data to CPU mapped memory.
        /// </summary>
        /// <typeparam name="T">Type of the data being written</typeparam>
        /// <param name="va">Virtual address to write the data into</param>
        /// <param name="value">Data to be written</param>
        public void Write<T>(ulong va, T value) where T : unmanaged
        {
            Write(va, MemoryMarshal.Cast<T, byte>(MemoryMarshal.CreateSpan(ref value, 1)));
        }

        /// <summary>
        /// Writes data to CPU mapped memory.
        /// </summary>
        /// <param name="va">Virtual address to write the data into</param>
        /// <param name="data">Data to be written</param>
        public void Write(ulong va, ReadOnlySpan<byte> data)
        {
            MarkRegionAsModified(va, (ulong)data.Length, true);

            if (IsContiguous(va, data.Length))
            {
                data.CopyTo(_backingMemory.GetSpan(GetPhysicalAddressInternal(va), data.Length));
            }
            else
            {
                int offset = 0, size;

                if ((va & PageMask) != 0)
                {
                    ulong pa = GetPhysicalAddressInternal(va);

                    size = Math.Min(data.Length, PageSize - (int)(va & PageMask));

                    data.Slice(0, size).CopyTo(_backingMemory.GetSpan(pa, size));

                    offset += size;
                }

                for (; offset < data.Length; offset += size)
                {
                    ulong pa = GetPhysicalAddressInternal(va + (ulong)offset);

                    size = Math.Min(data.Length - offset, PageSize);

                    data.Slice(offset, size).CopyTo(_backingMemory.GetSpan(pa, size));
                }
            }
        }

        /// <summary>
        /// Gets a read-only span of data from CPU mapped memory.
        /// </summary>
        /// <remarks>
        /// This may perform a allocation if the data is not contiguous in memory.
        /// For this reason, the span is read-only, you can't modify the data.
        /// </remarks>
        /// <param name="va">Virtual address of the data</param>
        /// <param name="size">Size of the data</param>
        /// <returns>A read-only span of the data</returns>
        public ReadOnlySpan<byte> GetSpan(ulong va, int size)
        {
            if (IsContiguous(va, size))
            {
                return _backingMemory.GetSpan(GetPhysicalAddressInternal(va), size);
            }
            else
            {
                Span<byte> data = new byte[size];

                ReadImpl(va, data);

                return data;
            }
        }

        /// <summary>
        /// Gets a reference for the given type at the specified virtual memory address.
        /// </summary>
        /// <remarks>
        /// The data must be located at a contiguous memory region.
        /// </remarks>
        /// <typeparam name="T">Type of the data to get the reference</typeparam>
        /// <param name="va">Virtual address of the data</param>
        /// <returns>A reference to the data in memory</returns>
        public ref T GetRef<T>(ulong va) where T : unmanaged
        {
            if (!IsContiguous(va, Unsafe.SizeOf<T>()))
            {
                ThrowMemoryNotContiguousException();
            }

            MarkRegionAsModified(va, (ulong)Unsafe.SizeOf<T>(), true);

            return ref _backingMemory.GetRef<T>(GetPhysicalAddressInternal(va));
        }

        private void ThrowMemoryNotContiguousException() => throw new MemoryNotContiguousException();

        // TODO: Remove that once we have proper 8-bits and 16-bits CAS.
        public ref T GetRefNoChecks<T>(ulong va) where T : unmanaged
        {
            MarkRegionAsModified(va, (ulong)Unsafe.SizeOf<T>(), true);

            return ref _backingMemory.GetRef<T>(GetPhysicalAddressInternal(va));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool IsContiguous(ulong va, int size)
        {
            if (!ValidateAddress(va))
            {
                return false;
            }

            ulong endVa = (va + (ulong)size + PageMask) & ~(ulong)PageMask;

            va &= ~(ulong)PageMask;

            int pages = (int)((endVa - va) / PageSize);

            for (int page = 0; page < pages - 1; page++)
            {
                if (!ValidateAddress(va + PageSize))
                {
                    return false;
                }

                if (GetPhysicalAddressInternal(va) + PageSize != GetPhysicalAddressInternal(va + PageSize))
                {
                    return false;
                }

                va += PageSize;
            }

            return true;
        }

        public (ulong address, ulong size)[] GetPhysicalRegions(ulong va, ulong size)
        {
            if (!ValidateAddress(va))
            {
                return null;
            }

            ulong endVa = (va + size + PageMask) & ~(ulong)PageMask;

            va &= ~(ulong)PageMask;

            int pages = (int)((endVa - va) / PageSize);

            List<(ulong, ulong)> regions = new List<(ulong, ulong)>();

            ulong regionStart = GetPhysicalAddressInternal(va);
            ulong regionSize = PageSize;

            for (int page = 0; page < pages - 1; page++)
            {
                if (!ValidateAddress(va + PageSize))
                {
                    return null;
                }

                ulong newPa = GetPhysicalAddressInternal(va + PageSize);

                if (GetPhysicalAddressInternal(va) + PageSize != newPa)
                {
                    regions.Add((regionStart, regionSize));
                    regionStart = newPa;
                    regionSize = 0;
                }

                va += PageSize;
                regionSize += PageSize;
            }

            regions.Add((regionStart, regionSize));

            return regions.ToArray();
        }

        private void ReadImpl(ulong va, Span<byte> data)
        {
            MarkRegionAsModified(va, (ulong)data.Length, false);
            int offset = 0, size;

            if ((va & PageMask) != 0)
            {
                ulong pa = GetPhysicalAddressInternal(va);

                size = Math.Min(data.Length, PageSize - (int)(va & PageMask));

                _backingMemory.GetSpan(pa, size).CopyTo(data.Slice(0, size));

                offset += size;
            }

            for (; offset < data.Length; offset += size)
            {
                ulong pa = GetPhysicalAddressInternal(va + (ulong)offset);

                size = Math.Min(data.Length - offset, PageSize);

                _backingMemory.GetSpan(pa, size).CopyTo(data.Slice(offset, size));
            }
        }

        /// <summary>
        /// Checks if the page at a given CPU virtual address.
        /// </summary>
        /// <param name="va">Virtual address to check</param>
        /// <returns>True if the address is mapped, false otherwise</returns>
        public bool IsMapped(ulong va)
        {
            if (!ValidateAddress(va))
            {
                return false;
            }

            return _pageTable.Read<ulong>((va / PageSize) * PteSize) != 0;
        }

        private bool ValidateAddress(ulong va)
        {
            return va < _addressSpaceSize;
        }
        
        /// <summary>
        /// Performs address translation of the address inside a CPU mapped memory range.
        /// </summary>
        /// <param name="va">Virtual address to be translated</param>
        /// <returns>The physical address</returns>
        public ulong GetPhysicalAddress(ulong va)
        {
            return GetPhysicalAddressInternal(va);
        }

        private ulong GetPhysicalAddressInternal(ulong va)
        {
            return PteToPa(_pageTable.Read<ulong>((va / PageSize) * PteSize) & ~(0xffffUL << 48)) + (va & PageMask);
        }

        private void MarkRegionProtection(ulong va, ulong size, MemoryProtection protection)
        {
            // Protection is inverted on software pages, since the default value is 0.
            protection = (~protection) & MemoryProtection.ReadAndWrite;

            long tag = (long)protection << 48;
            ulong endVa = (va + size + PageMask) & ~(ulong)PageMask;

            while (va < endVa)
            {
                ref long pageRef = ref _pageTable.GetRef<long>((va >> PageBits) * PteSize);

                long pte;

                do
                {
                    pte = Volatile.Read(ref pageRef);
                }
                while (Interlocked.CompareExchange(ref pageRef, (pte & ~(0xffffL << 48)) | tag, pte) != pte);

                va += PageSize;
            }
        }

        private void MarkRegionAsModified(ulong va, ulong size, bool write)
        {
            // We emulate guard pages for software memory access.
            // Ideally we'd just want to use the host guarded memory, but entering our exception handler
            // from managed and then calling a managed function would call a native -> managed transition
            // from a "managed" state, leading to an engine execution exception.

            long tag = (write ? 2L : 1L) << 48;

            ulong endVa = (va + size + PageMask) & ~(ulong)PageMask;

            while (va < endVa)
            {
                ref long pageRef = ref _pageTable.GetRef<long>((va >> PageBits) * PteSize);

                long pte;

                do
                {
                    pte = Volatile.Read(ref pageRef);

                    if ((pte & tag) != 0)
                    {
                        Tracking.VirtualMemoryEvent(va, size, true);
                        return;
                    }
                }
                while (Interlocked.CompareExchange(ref pageRef, pte & ~(0xffffL << 48), pte) != pte);

                va += PageSize;
            }
        }

        private ulong PaToPte(ulong pa)
        {
            return (ulong)_backingMemory.GetPointer(pa, PageSize).ToInt64();
        }

        private ulong PteToPa(ulong pte)
        {
            return (ulong)((long)pte - _backingMemory.Pointer.ToInt64());
        }

        public void Dispose()
        {
            _pageTable.Dispose();
        }
    }
}
