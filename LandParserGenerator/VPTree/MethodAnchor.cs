using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace VPTree
{
	// =======================
	// Domain: anchors & IO
	// =======================
	public sealed class MethodAnchor
	{
		public string Id;
		public string MethodNameNorm;
		public string ParentNameNorm;   // parent type
		public string ReturnTypeNorm;     // "T1,T2" if multi
		public List<Arg> Args;
		public int OrdinalInParent { get; set; }
		public Dictionary<string, double> NeighborBag { get; set; }

		public sealed class Arg
		{
			public string TypeNorm;
			public string NameNorm;
		}

		public static MethodAnchor FromRaw(
		    string id,
		    string methodName,
		    IEnumerable<Tuple<string, string>> args,   // (type, name)
		    IEnumerable<string> returns,
		    string parentName
		)
		{
			var a = new MethodAnchor
			{
				Id = id ?? "",
				MethodNameNorm = NormalizeName(methodName),
				ParentNameNorm = NormalizeTypeName(parentName ?? ""),
				ReturnTypeNorm = NormalizeReturnTypes(returns),
				Args = new List<Arg>()
			};
			if (args != null)
			{
				foreach (var t in args)
				{
					var ar = new Arg
					{
						TypeNorm = NormalizeTypeName(t != null ? (t.Item1 ?? "") : ""),
						NameNorm = NormalizeName(t != null ? (t.Item2 ?? "") : "")
					};
					a.Args.Add(ar);
				}
			}
			return a;
		}

		public static string NormalizeName(string s)
		{
			if (string.IsNullOrWhiteSpace(s)) return "";
			// split camelCase/PascalCase
			var withSpaces = System.Text.RegularExpressions.Regex.Replace(s, "([a-z0-9])([A-Z])", "$1 $2");
			withSpaces = withSpaces.Replace('_', ' ');
			var tokens = withSpaces.ToLowerInvariant().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
			return string.Join(" ", tokens);
		}

		public static string NormalizeTypeName(string t)
		{
			if (string.IsNullOrWhiteSpace(t)) return "";
			var s = t.Trim();
			s = System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ");
			s = s.Replace(" *", "*").Replace("* ", "*");
			s = s.Replace(" [", "[").Replace("[ ", "[");
			s = s.Replace(" ]", "]").Replace("] ", "]");
			s = s.Replace(" ,", ",").Replace(", ", ",");
			return s;
		}

		public static string NormalizeReturnTypes(IEnumerable<string> returns)
		{
			if (returns == null) return "";
			var arr = returns.Select(NormalizeTypeName).ToArray();
			if (arr.Length == 0) return "";
			return string.Join(",", arr);
		}
	}
}
