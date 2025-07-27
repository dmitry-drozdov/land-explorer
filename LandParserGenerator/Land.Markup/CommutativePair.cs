using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Land.Markup
{
	public readonly struct CommutativePairGuid
	{
		public Guid First { get; }
		public Guid Second { get; }

		public CommutativePairGuid(Guid a, Guid b)
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

		public override bool Equals(object obj) => obj is CommutativePairGuid other && Equals(other);

		public bool Equals(CommutativePairGuid other) => First.Equals(other.First) && Second.Equals(other.Second);

		public override int GetHashCode()
		{
			int hash1 = First.GetHashCode();
			int hash2 = Second.GetHashCode();
			return hash1 ^ hash2;
		}
	}
}
