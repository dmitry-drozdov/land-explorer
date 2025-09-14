using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace VPTree
{
	public static class AnchorKeys
	{
		// Каноническое представление "точного" совпадения:
		// receiver | method | type#name;type#name;... -> returnType1,returnType2,...
		public static string CanonExact(MethodAnchor a)
		{
			var sb = new StringBuilder(256);
			sb.Append(a.ParentNameNorm).Append('|');
			sb.Append(a.MethodNameNorm).Append('|');

			if (a.Args != null)
				for (int i = 0; i < a.Args.Count; i++)
				{
					var ar = a.Args[i];
					sb.Append(ar.TypeNorm).Append('#').Append(ar.NameNorm).Append(';');
				}

			sb.Append(">").Append(a.ReturnTypeNorm);
			return sb.ToString();
		}

		// 64-битный FNV-1a по UTF-16 младшему байту (дешево и быстро)
		public static ulong Fnv1a64(string s)
		{
			unchecked
			{
				const ulong off = 1469598103934665603UL;
				const ulong prm = 1099511628211UL;
				ulong h = off;
				for (int i = 0; i < s.Length; i++)
				{
					h ^= (byte)s[i];
					h *= prm;
					// при желании можно добавить (byte)(s[i] >> 8) для меньших коллизий
				}
				return h;
			}
		}
	}
}
