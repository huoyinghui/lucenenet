using System;
using Lucene.Net.Documents;

namespace Lucene.Net.Index
{
    /*
     * Licensed to the Apache Software Foundation (ASF) under one or more
     * contributor license agreements.  See the NOTICE file distributed with
     * this work for additional information regarding copyright ownership.
     * The ASF licenses this file to You under the Apache License, Version 2.0
     * (the "License"); you may not use this file except in compliance with
     * the License.  You may obtain a copy of the License at
     *
     *     http://www.apache.org/licenses/LICENSE-2.0
     *
     * Unless required by applicable law or agreed to in writing, software
     * distributed under the License is distributed on an "AS IS" BASIS,
     * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
     * See the License for the specific language governing permissions and
     * limitations under the License.
     */

    using BinaryDocValuesField = BinaryDocValuesField;
    using BinaryDocValuesUpdate = Lucene.Net.Index.DocValuesUpdate.BinaryDocValuesUpdate;
    using BytesRef = Lucene.Net.Util.BytesRef;
    using DocIdSetIterator = Lucene.Net.Search.DocIdSetIterator;
    using FixedBitSet = Lucene.Net.Util.FixedBitSet;
    using InPlaceMergeSorter = Lucene.Net.Util.InPlaceMergeSorter;
    using PackedInts = Lucene.Net.Util.Packed.PackedInts;
    using PagedGrowableWriter = Lucene.Net.Util.Packed.PagedGrowableWriter;
    using PagedMutable = Lucene.Net.Util.Packed.PagedMutable;

    /// <summary>
    /// A <seealso cref="AbstractDocValuesFieldUpdates"/> which holds updates of documents, of a single
    /// <seealso cref="BinaryDocValuesField"/>.
    ///
    /// @lucene.experimental
    /// </summary>
    internal class BinaryDocValuesFieldUpdates : AbstractDocValuesFieldUpdates
    {
        internal sealed class Iterator : AbstractDocValuesFieldUpdates.IIterator
        {
            private readonly PagedGrowableWriter offsets;
            private readonly int size;
            private readonly PagedGrowableWriter lengths;
            private readonly PagedMutable docs;
            private readonly FixedBitSet docsWithField;
            private long idx = 0; // long so we don't overflow if size == Integer.MAX_VALUE
            private int doc = -1;
            private readonly BytesRef value;
            private int offset, length;

            internal Iterator(int size, PagedGrowableWriter offsets, PagedGrowableWriter lengths, PagedMutable docs, BytesRef values, FixedBitSet docsWithField)
            {
                this.offsets = offsets;
                this.size = size;
                this.lengths = lengths;
                this.docs = docs;
                this.docsWithField = docsWithField;
                value = (BytesRef)values.Clone();
            }

            public object Value
            {
                get
                {
                    if (offset == -1)
                    {
                        return null;
                    }
                    else
                    {
                        value.Offset = offset;
                        value.Length = length;
                        return value;
                    }
                }
            }

            public int NextDoc()
            {
                if (idx >= size)
                {
                    offset = -1;
                    return doc = DocIdSetIterator.NO_MORE_DOCS;
                }
                doc = (int)docs.Get(idx);
                ++idx;
                while (idx < size && docs.Get(idx) == doc)
                {
                    ++idx;
                }
                // idx points to the "next" element
                long prevIdx = idx - 1;
                if (!docsWithField.Get((int)prevIdx))
                {
                    offset = -1;
                }
                else
                {
                    // cannot change 'value' here because nextDoc is called before the
                    // value is used, and it's a waste to clone the BytesRef when we
                    // obtain the value
                    offset = (int)offsets.Get(prevIdx);
                    length = (int)lengths.Get(prevIdx);
                }
                return doc;
            }

            public int Doc
            {
                get { return doc; }
            }

            public void Reset()
            {
                doc = -1;
                offset = -1;
                idx = 0;
            }
        }

        private FixedBitSet docsWithField;
        private PagedMutable docs;
        private PagedGrowableWriter offsets, lengths;
        private BytesRef values;
        private int size;

        public BinaryDocValuesFieldUpdates(string field, int maxDoc)
            : base(field, DocValuesFieldUpdates.Type.BINARY)
        {
            docsWithField = new FixedBitSet(64);
            docs = new PagedMutable(1, 1024, PackedInts.BitsRequired(maxDoc - 1), PackedInts.COMPACT);
            offsets = new PagedGrowableWriter(1, 1024, 1, PackedInts.FAST);
            lengths = new PagedGrowableWriter(1, 1024, 1, PackedInts.FAST);
            values = new BytesRef(16); // start small
            size = 0;
        }

        public override void Add(int doc, object value)
        {
            // TODO: if the Sorter interface changes to take long indexes, we can remove that limitation
            if (size == int.MaxValue)
            {
                throw new InvalidOperationException("cannot support more than Integer.MAX_VALUE doc/value entries");
            }

            BytesRef val = (BytesRef)value;
            if (val == null)
            {
                val = BinaryDocValuesUpdate.MISSING;
            }

            // grow the structures to have room for more elements
            if (docs.Size == size)
            {
                docs = docs.Grow(size + 1);
                offsets = offsets.Grow(size + 1);
                lengths = lengths.Grow(size + 1);
                docsWithField = FixedBitSet.EnsureCapacity(docsWithField, (int)docs.Size);
            }

            if (val != BinaryDocValuesUpdate.MISSING)
            {
                // only mark the document as having a value in that field if the value wasn't set to null (MISSING)
                docsWithField.Set(size);
            }

            docs.Set(size, doc);
            offsets.Set(size, values.Length);
            lengths.Set(size, val.Length);
            values.Append(val);
            ++size;
        }

        public override AbstractDocValuesFieldUpdates.IIterator GetIterator()
        {
            PagedMutable docs = this.docs;
            PagedGrowableWriter offsets = this.offsets;
            PagedGrowableWriter lengths = this.lengths;
            BytesRef values = this.values;
            FixedBitSet docsWithField = this.docsWithField;
            new InPlaceMergeSorterAnonymousInnerClassHelper(this, docs, offsets, lengths, docsWithField).Sort(0, size);

            return new Iterator(size, offsets, lengths, docs, values, docsWithField);
        }

        private class InPlaceMergeSorterAnonymousInnerClassHelper : InPlaceMergeSorter
        {
            private readonly BinaryDocValuesFieldUpdates outerInstance;

            private PagedMutable docs;
            private PagedGrowableWriter offsets;
            private PagedGrowableWriter lengths;
            private FixedBitSet docsWithField;

            public InPlaceMergeSorterAnonymousInnerClassHelper(BinaryDocValuesFieldUpdates outerInstance, PagedMutable docs, PagedGrowableWriter offsets, PagedGrowableWriter lengths, FixedBitSet docsWithField)
            {
                this.outerInstance = outerInstance;
                this.docs = docs;
                this.offsets = offsets;
                this.lengths = lengths;
                this.docsWithField = docsWithField;
            }

            protected override void Swap(int i, int j)
            {
                long tmpDoc = docs.Get(j);
                docs.Set(j, docs.Get(i));
                docs.Set(i, tmpDoc);

                long tmpOffset = offsets.Get(j);
                offsets.Set(j, offsets.Get(i));
                offsets.Set(i, tmpOffset);

                long tmpLength = lengths.Get(j);
                lengths.Set(j, lengths.Get(i));
                lengths.Set(i, tmpLength);

                bool tmpBool = docsWithField.Get(j);
                if (docsWithField.Get(i))
                {
                    docsWithField.Set(j);
                }
                else
                {
                    docsWithField.Clear(j);
                }
                if (tmpBool)
                {
                    docsWithField.Set(i);
                }
                else
                {
                    docsWithField.Clear(i);
                }
            }

            protected override int Compare(int i, int j)
            {
                int x = (int)docs.Get(i);
                int y = (int)docs.Get(j);
                return (x < y) ? -1 : ((x == y) ? 0 : 1);
            }
        }

        public override void Merge(AbstractDocValuesFieldUpdates other)
        {
            BinaryDocValuesFieldUpdates otherUpdates = (BinaryDocValuesFieldUpdates)other;
            int newSize = size + otherUpdates.size;
            if (newSize > int.MaxValue)
            {
                throw new InvalidOperationException("cannot support more than Integer.MAX_VALUE doc/value entries; size=" + size + " other.size=" + otherUpdates.size);
            }
            docs = docs.Grow(newSize);
            offsets = offsets.Grow(newSize);
            lengths = lengths.Grow(newSize);
            docsWithField = FixedBitSet.EnsureCapacity(docsWithField, (int)docs.Size);
            for (int i = 0; i < otherUpdates.size; i++)
            {
                int doc = (int)otherUpdates.docs.Get(i);
                if (otherUpdates.docsWithField.Get(i))
                {
                    docsWithField.Set(size);
                }
                docs.Set(size, doc);
                offsets.Set(size, values.Length + otherUpdates.offsets.Get(i)); // correct relative offset
                lengths.Set(size, otherUpdates.lengths.Get(i));
                ++size;
            }
            values.Append(otherUpdates.values);
        }

        public override bool Any()
        {
            return size > 0;
        }
    }
}