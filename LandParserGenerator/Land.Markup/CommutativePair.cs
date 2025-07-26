using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Land.Markup
{
	public readonly struct CommutativePair<T> : IEquatable<CommutativePair<T>>
		where T : IComparable<T>
	{
		public T First { get; }
		public T Second { get; }

		public CommutativePair(T a, T b)
		{
			if (a.CompareTo(b) <= 0)
			{
				First = a;
				Second = b;
			}
			else
			{
				First = b;
				Second = a;
			}
		}

		public override bool Equals(object obj) => obj is CommutativePair<T> other && Equals(other);

		public bool Equals(CommutativePair<T> other) =>
			EqualityComparer<T>.Default.Equals(First, other.First) &&
			EqualityComparer<T>.Default.Equals(Second, other.Second);

		public override int GetHashCode()
		{
			int hash1 = First?.GetHashCode() ?? 0;
			int hash2 = Second?.GetHashCode() ?? 0;
			return hash1 ^ hash2;
		}
	}
}
