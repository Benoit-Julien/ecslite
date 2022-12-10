using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Leopotam.EcsLite
{
    public interface IEcsBuffer : IEcsBase
    {
        unsafe void Resize(BufferInfo* info);
    }

    internal static unsafe class EcsBufferUtility
    {
        private struct BufferInfoStartIndexComparer : IComparer<BufferInfo>
        {
            public int Compare(BufferInfo x, BufferInfo y)
            {
                return x.StartIndex.CompareTo(y.StartIndex);
            }
        }

        internal static T* ArrayElementAsPtr< T >(T[] array, int index)
            where T : unmanaged
        {
            fixed (T* ptr = array)
            {
                return ArrayElementAsPtr(ptr, index);
            }
        }

        internal static T* ArrayElementAsPtr< T >(T* array, int index)
            where T : unmanaged
        {
            return (T*)((byte*)array + index * sizeof(T));
        }

        internal static int RemoveAt< T >(T[] array, int index)
            where T : unmanaged
        {
            return RemoveRange(array, index, 1);
        }

        internal static int RemoveRange< T >(T[] array, int index, int count)
            where T : unmanaged
        {
            fixed (T* ptr = array)
            {
                return RemoveRange(ptr, array.Length, index, count);
            }
        }

        internal static int RemoveAt< T >(T* array, int lenght, int index)
            where T : unmanaged
        {
            return RemoveRange(array, lenght, index, 1);
        }

        internal static int RemoveRange< T >(T* array, int lenght, int index, int count)
            where T : unmanaged
        {
            int copyFrom = Math.Min(index + count, lenght);
            void* dst = ArrayElementAsPtr(array, index);
            void* src = ArrayElementAsPtr(array, copyFrom);
            UnsafeUtility.MemCpy(dst, src, (lenght - copyFrom) * sizeof(T));

            T* toClear = ArrayElementAsPtr(array, lenght - count);
            UnsafeUtility.MemClear(toClear, count * sizeof(T));
            return lenght - count;
        }

        internal static void SortBufferInfoArray(BufferInfo[] array, int lenght)
        {
            Array.Sort(array, 0, lenght, s_comparer);
        }

        private static BufferInfoStartIndexComparer s_comparer;
    }

    public struct BufferInfo : IEquatable<BufferInfo>
    {
        public int Capacity;
        public int Length;
        public int StartIndex;

        public bool Equals(BufferInfo other)
        {
            return Capacity      == other.Capacity
                   && Length     == other.Length
                   && StartIndex == other.StartIndex;
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(Capacity, Length, StartIndex);
        }

        public static bool operator ==(BufferInfo left, BufferInfo right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(BufferInfo left, BufferInfo right)
        {
            return !left.Equals(right);
        }
    }

    public unsafe struct Buffer< T > where T : unmanaged
    {
        internal T* _data;
        internal BufferInfo* _info;
        internal IEcsBuffer _ecsBuffer;

        public int Length => _info->Length;
        public int Capacity => _info->Capacity;

        internal Buffer(T* data, BufferInfo* info, EcsBuffer<T> ecsBuffer)
        {
            _data = EcsBufferUtility.ArrayElementAsPtr<T>(data, info->StartIndex);
            _info = info;
            _ecsBuffer = ecsBuffer;
        }

        public ref T this[int index] {
            get { return ref UnsafeUtility.ArrayElementAsRef<T>(_data, index); }
        }

        public void Add(in T value)
        {
            var idx = _info->Length;

            if (_info->Length + 1 > _info->Capacity)
                Resize(idx + 1);
            _info->Length++;

            UnsafeUtility.WriteArrayElement(_data, idx, value);
        }

        public void AddRange(NativeArray<T> array)
        {
            AddRange(array.GetUnsafeReadOnlyPtr(), array.Length);
        }

        public void AddRange(void* ptr, int count)
        {
            var idx = _info->Length;

            if (_info->Length + count > _info->Capacity)
                Resize(_info->Length + count);
            else
                _info->Length += count;

            void* dst = EcsBufferUtility.ArrayElementAsPtr(_data, idx);
            UnsafeUtility.MemCpy(dst, ptr, count * sizeof(T));
        }

        public void RemoveAt(int index)
            => _info->Length = EcsBufferUtility.RemoveAt(_data, _info->Length, index);

        public void RemoveRange(int index, int count)
            => _info->Length = EcsBufferUtility.RemoveRange(_data, _info->Length, index, count);

        public void Resize(int size)
        {
            while (_info->Capacity < size)
                _ecsBuffer.Resize(_info);
        }

        public Buffer<U> Reinterpret< U >() where U : unmanaged
        {
            if (UnsafeUtility.SizeOf<T>() != UnsafeUtility.SizeOf<U>())
                throw new InvalidOperationException(string.Format("Types {0} and {1} are different sizes - direct reinterpretation is not possible.", typeof(T), typeof(U)));

            return new Buffer<U> {
                _data = (U*)_data,
                _info = _info,
                _ecsBuffer = _ecsBuffer
            };
        }
    }

#if ENABLE_IL2CPP
    [Il2CppSetOption (Option.NullChecks, false)]
    [Il2CppSetOption (Option.ArrayBoundsChecks, false)]
#endif
    public sealed unsafe class EcsBuffer< T > : IEcsBuffer where T : unmanaged
    {
        readonly Type _type;
        readonly EcsWorld _world;
        readonly short _id;
        T[] _denseItems;
        BufferInfo[] _sparseItems;
        int _sparseItemsCount;
        BufferInfo[] _recycledItems;
        int _recycledItemsCount;

        internal EcsBuffer(EcsWorld world, short id, int denseCapacity, int sparseCapacity, int recycledCapacity)
        {
            _type = typeof(T);
            _world = world;
            _id = id;
            _denseItems = new T[denseCapacity];
            _sparseItems = new BufferInfo[sparseCapacity];
            _sparseItemsCount = 0;
            _recycledItems = new BufferInfo[recycledCapacity];
            AddToRecycledItems(new BufferInfo {
                Capacity = denseCapacity,
                Length = 0,
                StartIndex = 0
            });
        }

#if UNITY_2020_3_OR_NEWER
        [UnityEngine.Scripting.Preserve]
#endif
        void ReflectionSupportHack()
        {
            _world.GetBuffer<T>();
            _world.Filter<T>().Exc<T>().End();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public EcsWorld GetWorld() => _world;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int GetId() => _id;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Type GetComponentType() => _type;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Resize(int capacity)
        {
            Array.Resize(ref _sparseItems, capacity);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Buffer<T> Add(int entity, int size = 16)
        {
#if DEBUG && !LEOECSLITE_NO_SANITIZE_CHECKS
            if (!_world.IsEntityAliveInternal(entity))
                throw new Exception("Cant touch destroyed entity.");
            if (_sparseItems[entity] != default)
                throw new Exception($"Component \"{typeof(T).Name}\" already attached to entity.");
#endif
            if (_recycledItemsCount == 0)
            {
                int beforeLength = _denseItems.Length;
                Array.Resize(ref _denseItems, _denseItems.Length << 1);
                AddToRecycledItems(new BufferInfo {
                    Capacity = _denseItems.Length - beforeLength,
                    Length = 0,
                    StartIndex = beforeLength
                });
            }

            int recycledIndex = GetRecycledItemIndex(size);
            var recycledInfo = EcsBufferUtility.ArrayElementAsPtr(_recycledItems, recycledIndex);
            var info = EcsBufferUtility.ArrayElementAsPtr(_sparseItems, entity);
            info->Capacity = size;
            info->Length = 0;
            info->StartIndex = recycledInfo->StartIndex;
            if (recycledInfo->Capacity - info->Capacity <= 0)
                RemoveToRecycledItems(recycledIndex);
            else
            {
                recycledInfo->Capacity = recycledInfo->Capacity - info->Capacity;
                recycledInfo->StartIndex = info->StartIndex     + info->Capacity;
            }
            _world.OnEntityChangeInternal(entity, _id, true);
            _world.AddComponentToRawEntityInternal(entity, _id);
#if DEBUG || LEOECSLITE_WORLD_EVENTS
            _world.RaiseEntityChangeEvent(entity);
#endif
            return Get(entity);
        }

        object IEcsBase.GetRaw(int entity)
        {
            return Get(entity);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Buffer<T> Get(int entity)
        {
#if DEBUG && !LEOECSLITE_NO_SANITIZE_CHECKS
            if (!_world.IsEntityAliveInternal(entity))
                throw new Exception("Cant touch destroyed entity.");
            if (_sparseItems[entity] == default)
                throw new Exception($"Cant get \"{typeof(T).Name}\" component - not attached.");
#endif
            fixed (T* data = _denseItems)
            {
                var bufferInfo = EcsBufferUtility.ArrayElementAsPtr(_sparseItems, entity);
                return new Buffer<T>(data, bufferInfo, this);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Has(int entity)
        {
#if DEBUG && !LEOECSLITE_NO_SANITIZE_CHECKS
            if (!_world.IsEntityAliveInternal(entity))
                throw new Exception("Cant touch destroyed entity.");
#endif
            return _sparseItems[entity] != default;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Del(int entity)
        {
#if DEBUG && !LEOECSLITE_NO_SANITIZE_CHECKS
            if (!_world.IsEntityAliveInternal(entity))
                throw new Exception("Cant touch destroyed entity.");
#endif
            if (_sparseItems[entity] != default)
            {
                _world.OnEntityChangeInternal(entity, _id, false);
                AddToRecycledItems(_sparseItems[entity]);
                _sparseItems[entity] = default;
                SortRecycledItem();
                MergeRecycledItem();

                var componentsCount = _world.RemoveComponentFromRawEntityInternal(entity, _id);
#if DEBUG || LEOECSLITE_WORLD_EVENTS
                _world.RaiseEntityChangeEvent(entity);
#endif
                if (componentsCount == 0)
                    _world.DelEntity(entity);
            }
        }

        public void Resize(BufferInfo* info)
        {
            int targetSize = info->Capacity << 1;
            
            AddToRecycledItems(*info);
            SortRecycledItem();
            MergeRecycledItem();
            
            int recycledIndex = GetRecycledItemIndex(targetSize);
            var recycledInfo = EcsBufferUtility.ArrayElementAsPtr(_recycledItems, recycledIndex);

            void* src = EcsBufferUtility.ArrayElementAsPtr(_denseItems, info->StartIndex);
            void* dst = EcsBufferUtility.ArrayElementAsPtr(_denseItems, recycledInfo->StartIndex);
            UnsafeUtility.MemMove(dst, src, info->Length);
            
            info->Capacity = targetSize;
            info->StartIndex = recycledInfo->StartIndex;
            if (recycledInfo->Capacity - info->Capacity <= 0)
                RemoveToRecycledItems(recycledIndex);
            else
            {
                recycledInfo->Capacity = recycledInfo->Capacity - info->Capacity;
                recycledInfo->StartIndex = info->StartIndex     + info->Capacity;
            }

            SortRecycledItem();
            MergeRecycledItem();
        }

        private int GetRecycledItemIndex(int size)
        {
            for (var i = 0; i < _recycledItemsCount; ++i)
            {
                if (_recycledItems[i].Capacity >= size)
                    return i;
            }

            int beforeLength = _denseItems.Length;
            Array.Resize(ref _denseItems, _denseItems.Length << 1);
            AddToRecycledItems(new BufferInfo {
                Capacity = _denseItems.Length - beforeLength,
                Length = 0,
                StartIndex = beforeLength
            });
            MergeRecycledItem();
            return _recycledItemsCount - 1;
        }

        private void AddToRecycledItems(in BufferInfo item)
        {
            if (_recycledItemsCount == _recycledItems.Length)
                Array.Resize(ref _recycledItems, _recycledItems.Length << 1);
            _recycledItems[_recycledItemsCount] = item;
            _recycledItemsCount++;
        }

        private void RemoveToRecycledItems(int index)
        {
            fixed (BufferInfo* ptr = _recycledItems)
                _recycledItemsCount = EcsBufferUtility.RemoveAt(ptr, _recycledItemsCount, index);
        }

        public void SortRecycledItem() => EcsBufferUtility.SortBufferInfoArray(_recycledItems, _recycledItemsCount);

        private void MergeRecycledItem()
        {
            var i = 0;
            while (i < _recycledItemsCount - 1)
            {
                var current = EcsBufferUtility.ArrayElementAsPtr(_recycledItems, i);
                var next = EcsBufferUtility.ArrayElementAsPtr(_recycledItems, i + 1);

                if (next->StartIndex == current->StartIndex + current->Capacity)
                {
                    current->Capacity += next->Capacity;
                    RemoveToRecycledItems(i + 1);
                }
                else
                    i++;
            }
        }
    }
}