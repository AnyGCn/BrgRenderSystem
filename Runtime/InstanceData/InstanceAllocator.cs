﻿using System;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Assertions;

namespace BrgRenderSystem
{
    //@ Add instance version to detect dangling instance handles.
    internal struct InstanceHandle : IEquatable<InstanceHandle>, IComparable<InstanceHandle>
    {
        // Don't use this index to reference GPU data. This index is to reference CPU data only.
        // To reference GPU data convert InstanceHandle to GPUInstanceIndex.
        public int index { get; private set; }

        // This is unique instance index for each instance type.
        public int instanceIndex => index; // >> InstanceTypeInfo.kInstanceTypeBitCount;

        // We store type bits as lower bits because this makes max InstanceHandle index bounded by how many instances we have.
        // So you can allocate directly indexed arrays. This is fine as long as we have only 1 to 4 instance types.
        // If we put type bits in higher bits then we might want to make CPUInstanceData sparse set InstanceIndices table to be paged.
        // public InstanceType type => (InstanceType)(index & InstanceTypeInfo.kInstanceTypeMask);

        public bool valid => index != -1;
        public static readonly InstanceHandle Invalid = new InstanceHandle() { index = -1 };
        public static InstanceHandle Create(int instanceIndex) { return new InstanceHandle() { index = instanceIndex }; }
        public static InstanceHandle FromInt(int value) { return new InstanceHandle() { index = value }; }
        public bool Equals(InstanceHandle other) => index == other.index;
        public int CompareTo(InstanceHandle other) { return index.CompareTo(other.index); }
        public override int GetHashCode() { return index; }
    }

    internal struct SharedInstanceHandle : IEquatable<SharedInstanceHandle>, IComparable<SharedInstanceHandle>
    {
        public int index { get; set; }
        public bool valid => index != -1;
        public static readonly SharedInstanceHandle Invalid = new SharedInstanceHandle() { index = -1 };
        public bool Equals(SharedInstanceHandle other) => index == other.index;
        public int CompareTo(SharedInstanceHandle other) { return index.CompareTo(other.index); }
        public override int GetHashCode() { return index; }
    }

    internal struct GPUInstanceIndex : IEquatable<GPUInstanceIndex>, IComparable<GPUInstanceIndex>
    {
        private int data;

        public int windowIndex => data >> 16;
        
        public int index => data & 0xFFFF;
        
        public bool valid => index != -1;

        public GPUInstanceIndex(int index, int window = 0)
        {
            Assert.IsTrue(index < 0xFFFF && window < 0x8000);
            data = (window << 16) | index;
        }
        
        public static readonly GPUInstanceIndex Invalid = new GPUInstanceIndex() { data = -1 };
        public bool Equals(GPUInstanceIndex other) => data == other.data;

        public int CompareTo(GPUInstanceIndex other)
        {
            return data.CompareTo(other.data);
        }
        public override int GetHashCode() { return data; }
    }

    internal struct InstanceAllocator : IDisposable
    {
        private NativeArray<int> m_StructData;
        private NativeList<int> m_FreeInstances;

        public int length { get => m_StructData[0]; set => m_StructData[0] = value; }
        public bool valid => m_StructData.IsCreated;

        public void Initialize()
        {
            m_StructData = new NativeArray<int>(1, Allocator.Persistent);
            m_FreeInstances = new NativeList<int>(Allocator.Persistent);
        }

        public void Dispose()
        {
            m_StructData.Dispose();
            m_FreeInstances.Dispose();
        }

        public int AllocateInstance()
        {
            int instance;

            if (m_FreeInstances.Length > 0)
            {
                instance = m_FreeInstances[m_FreeInstances.Length - 1];
                m_FreeInstances.RemoveAtSwapBack(m_FreeInstances.Length - 1);
            }
            else
            {
                instance = length;
                length += 1;
            }

            return instance;
        }

        public void FreeInstance(int instance)
        {
            //@ This is a bit weak validation. Need something better but fast.
            Assert.IsTrue(instance >= 0 && instance < length);
            m_FreeInstances.Add(instance);
        }

        public int GetNumAllocated()
        {
            return length - m_FreeInstances.Length;
        }
    }

    internal unsafe struct InstanceAllocators : IDisposable
    {
        private InstanceAllocator m_InstanceAlloc;
        // private InstanceAllocator m_InstanceAlloc_SpeedTree;
        private InstanceAllocator m_SharedInstanceAlloc;

        public void Initialize()
        {
            //@ Will keep it as two separate allocators for two types for now. Nested native containers are not allowed in burst.
            m_InstanceAlloc = new InstanceAllocator();
            // m_InstanceAlloc_SpeedTree = new InstanceAllocator();
            m_InstanceAlloc.Initialize();
            // m_InstanceAlloc_SpeedTree.Initialize((int)InstanceType.SpeedTree, InstanceTypeInfo.kMaxInstanceTypesCount);

            m_SharedInstanceAlloc = new InstanceAllocator();
            m_SharedInstanceAlloc.Initialize();
        }

        public unsafe void Dispose()
        {
            m_InstanceAlloc.Dispose();
            m_SharedInstanceAlloc.Dispose();
        }

        private InstanceAllocator GetInstanceAllocator()
        {
            return m_InstanceAlloc;
        }

        public int GetInstanceHandlesLength()
        {
            return GetInstanceAllocator().length;
        }

        public int GetInstancesLength()
        {
            return GetInstanceAllocator().GetNumAllocated();
        }

        public InstanceHandle AllocateInstance()
        {
            return InstanceHandle.FromInt(GetInstanceAllocator().AllocateInstance());
        }

        public void FreeInstance(InstanceHandle instance)
        {
            Assert.IsTrue(instance.valid);
            GetInstanceAllocator().FreeInstance(instance.index);
        }

        public unsafe SharedInstanceHandle AllocateSharedInstance()
        {
            return new SharedInstanceHandle { index = m_SharedInstanceAlloc.AllocateInstance() };
        }

        public void FreeSharedInstance(SharedInstanceHandle instance)
        {
            Assert.IsTrue(instance.valid);
            m_SharedInstanceAlloc.FreeInstance(instance.index);
        }
    }
}
