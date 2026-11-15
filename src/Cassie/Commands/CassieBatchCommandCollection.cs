using System.Collections;
using System.Data.Common;

namespace Cntryl.Cassie.Commands
{
    /// <summary>Stores the commands of a <see cref="CassieBatch"/> in execution order.</summary>
    public sealed class CassieBatchCommandCollection : DbBatchCommandCollection
    {
        private readonly List<CassieBatchCommand> _items = [];

        /// <inheritdoc />
        public override int Count => _items.Count;

        /// <inheritdoc />
        public override bool IsReadOnly => false;

        /// <summary>Gets or sets the command at the specified index.</summary>
        public new CassieBatchCommand this[int index]
        {
            get => _items[index];
            set => _items[index] = Require(value);
        }

        /// <summary>Appends a command that has the given SQL text and returns it so parameters can be added.</summary>
        /// <param name="commandText">The SQL text of the new command.</param>
        /// <returns>The command that was added.</returns>
        public CassieBatchCommand Add(string commandText)
        {
            CassieBatchCommand command = new(commandText);
            _items.Add(command);
            return command;
        }

        /// <inheritdoc />
        public override void Add(DbBatchCommand item)
        {
            _items.Add(Require(item));
        }

        /// <inheritdoc />
        public override void Clear()
        {
            _items.Clear();
        }

        /// <inheritdoc />
        public override bool Contains(DbBatchCommand item)
        {
            return item is CassieBatchCommand command && _items.Contains(command);
        }

        /// <inheritdoc />
        public override void CopyTo(DbBatchCommand[] array, int arrayIndex)
        {
            ArgumentNullException.ThrowIfNull(array);
            ((ICollection)_items).CopyTo(array, arrayIndex);
        }

        /// <inheritdoc />
        public override IEnumerator<DbBatchCommand> GetEnumerator()
        {
            return _items.GetEnumerator();
        }

        /// <inheritdoc />
        public override int IndexOf(DbBatchCommand item)
        {
            return item is CassieBatchCommand command ? _items.IndexOf(command) : -1;
        }

        /// <inheritdoc />
        public override void Insert(int index, DbBatchCommand item)
        {
            _items.Insert(index, Require(item));
        }

        /// <inheritdoc />
        public override bool Remove(DbBatchCommand item)
        {
            return item is CassieBatchCommand command && _items.Remove(command);
        }

        /// <inheritdoc />
        public override void RemoveAt(int index)
        {
            _items.RemoveAt(index);
        }

        /// <inheritdoc />
        protected override DbBatchCommand GetBatchCommand(int index)
        {
            return _items[index];
        }

        /// <inheritdoc />
        protected override void SetBatchCommand(int index, DbBatchCommand batchCommand)
        {
            _items[index] = Require(batchCommand);
        }

        internal IReadOnlyList<CassieBatchCommand> Items => _items;

        private static CassieBatchCommand Require(DbBatchCommand? item)
        {
            ArgumentNullException.ThrowIfNull(item);
            return item as CassieBatchCommand
                ?? throw new InvalidCastException($"A {nameof(CassieBatch)} accepts only {nameof(CassieBatchCommand)} instances.");
        }
    }
}
