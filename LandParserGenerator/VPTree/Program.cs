// Target: .NET Framework 4.6.1
// NuGet: Newtonsoft.Json

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using Newtonsoft.Json;

namespace VPTree
{
	// =======================
	// Example
	// =======================
	public static class Example
	{
		public static void Main(string[] args)
		{
			Run();
		}
		public static void Run()
		{
			var anchors = new List<MethodAnchor>
			    {
				MethodAnchor.FromRaw("old#1", 0, 100, "ComputeHash",
				    new [] { Tuple.Create("int","a"), Tuple.Create("string","b") },
				    new [] { "int" }, "Hasher"),

				MethodAnchor.FromRaw("old#2", 0, 100, "FindUser",
				    new [] { Tuple.Create("int","id") },
				    new [] { "*User", "error" }, "Repo"),

				MethodAnchor.FromRaw("old#3", 0, 100, "WriteJSON",
				    new [] { Tuple.Create("io.Writer","w"), Tuple.Create("any","v"), Tuple.Create("bool","indent") },
				    new [] { "error" }, "Encoder"),
			    };

			AnchorsIO.SaveJson("anchors.json", anchors);
			var loaded = AnchorsIO.LoadJson("anchors.json");

			var weights = new Dist.Weights();
			var rebinder = new Rebinder(loaded, weights, null);

			var q = MethodAnchor.FromRaw("new#tmp", 0, 100, "FindUser",
			    new[] { Tuple.Create("int", "a"), Tuple.Create("string", "b") },
			    new[] { "int" }, "Hasher");

			var knn = rebinder.Query(q, 3);
			for (int i = 0; i < knn.Count; i++)
			{
				var k = knn[i];
				var cand = loaded[k.Index];
				Console.WriteLine(string.Format("#{0}: {1}  {2}  dist={3:0.0000}",
				    i + 1, cand.Id, cand.MethodNameNorm, k.Dist));
			}
		}
	}
}
