using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VPTree;

// =====================================================================================
// VPTreeBench — консольный стенд для экспериментов над VPTreeLib (C#, «как в продакшене»).
// Запускается из Python-скриптов в markup/experiments/*; результаты пишет в CSV.
//
//   axioms       — аксиомы метрики на реальном корпусе (structured / random / arity тройки)
//   knn          — VP-дерево vs полный перебор: точность, время, число вычислений расстояния
//   pairs        — расстояния (и компоненты) для заданных пар: сверка с Python-портом
//   determinism  — повторные построения индекса: побайтовая идентичность результата
//
// Все числа — Invariant culture, seed фиксирован, вход канонизируется CanonicalOrder.Sort.
// =====================================================================================

static class Program
{
	static int Main(string[] argv)
	{
		CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
		CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
		if (argv.Length == 0) { Usage(); return 2; }
		var cmd = argv[0];
		var args = ParseArgs(argv.Skip(1));
		try
		{
			switch (cmd)
			{
				case "axioms": return Axioms(args);
				case "knn": return Knn(args);
				case "knn-batch": return KnnBatch(args);
				case "pairs": return Pairs(args);
				case "determinism": return Determinism(args);
				case "build-cost": return BuildCost(args);
				default: Usage(); return 2;
			}
		}
		catch (Exception ex)
		{
			Console.Error.WriteLine("[fatal] " + ex);
			return 1;
		}
	}

	// build-cost: стоимость подготовки на одно изменение файла — мешки соседей + построение VP-дерева
	// по индексу (новой версии) для каждой пары манифеста knn-batch; медиана по повторам.
	// Время дерева измеряется без счётчика вызовов (Interlocked в параллельном построении искажает время);
	// число вычислений расстояния и глубина — отдельным построением со счётчиком.
	// Параллелизм построения = Environment.ProcessorCount (переопределяется DOTNET_PROCESSOR_COUNT).
	static int BuildCost(Dictionary<string, string> a)
	{
		var manifest = JArray.Parse(File.ReadAllText(Get(a, "manifest") ?? throw new ArgumentException("--manifest")));
		var w = MakeWeights(a);
		var bags = BagKind(a);
		int repeats = GetInt(a, "repeats", 7);
		var outPath = Get(a, "out") ?? throw new ArgumentException("--out");
		Func<MethodAnchor, MethodAnchor, double> plain = (x, y) => Dist.AnchorDistance(x, y, w);
		var rows = new List<string>();
		var sw = new Stopwatch();
		bool warmed = false;
		foreach (var job in manifest.OfType<JObject>())
		{
			string label = job["label"]?.ToString() ?? "";
			var index = CanonicalOrder.Sort(LoadAnchors(job["index"]!.ToString()));
			if (index.Count == 0) continue;
			if (!warmed)
			{
				for (int i = 0; i < 5; i++) { AnchorsIO.BuildNeighborBags(index, bags, 4); _ = new VPTree<MethodAnchor>(index, plain, 42); }
				warmed = true;
			}
			var bagsMs = new List<double>(); var treeMs = new List<double>();
			for (int r = 0; r < repeats; r++)
			{
				sw.Restart(); AnchorsIO.BuildNeighborBags(index, bags, 4); sw.Stop(); bagsMs.Add(sw.Elapsed.TotalMilliseconds);
				sw.Restart(); _ = new VPTree<MethodAnchor>(index, plain, 42); sw.Stop(); treeMs.Add(sw.Elapsed.TotalMilliseconds);
			}
			var c = new Counter();
			var counted = new VPTree<MethodAnchor>(index, Counted(w, c), 42);
			rows.Add(Csv(label, index.Count, repeats, Median(bagsMs), Median(treeMs), c.Value, counted.BuildDepth, bagsMs.Min(), treeMs.Min(), Environment.ProcessorCount));
		}
		AppendCsv(outPath, "label,n_index,repeats,bags_ms,tree_ms,tree_calls,tree_depth,bags_ms_min,tree_ms_min,processor_count", rows);
		Console.Error.WriteLine($"[build-cost] {rows.Count} jobs, repeats={repeats}, processors={Environment.ProcessorCount}");
		return 0;
	}

	static void Usage()
	{
		Console.Error.WriteLine(@"usage:
  VPTreeBench axioms --anchors a.json [--triples 100000] [--seed 42] [--mode constant|legacy] [--bags rich|server|none] [--out res.csv]
  VPTreeBench knn (--corpus corpora.json --N 3000 | --old old.json --new new.json [--gt gt.json])
                  [--k 1,5,20] [--mode constant|legacy] [--weights 0.15,0.30,0.35,0.15,0.05] [--argmax 8]
                  [--bags rich|server|none] [--queries 500] [--seed 42] [--out summary.csv] [--per-query pq.csv] [--label text]
  VPTreeBench pairs --anchors a.json --pairs pairs.csv [--mode ...] [--bags ...] [--weights ...] --out dist.csv
  VPTreeBench determinism --anchors a.json [--repeats 20] [--queries 200] [--out res.csv]
  VPTreeBench build-cost --manifest jobs.json [--repeats 7] [--mode ...] [--bags ...] [--weights ...] --out cost.csv
  knn-batch also accepts --window N (neighbor bag window, default 4)");
	}

	// ----------------------------------------------------------------- helpers

	static Dictionary<string, string> ParseArgs(IEnumerable<string> a)
	{
		var d = new Dictionary<string, string>(StringComparer.Ordinal);
		string key = null;
		foreach (var s in a)
		{
			if (s.StartsWith("--")) { key = s.Substring(2); d[key] = "true"; }
			else if (key != null) { d[key] = s; key = null; }
		}
		return d;
	}

	static string Get(Dictionary<string, string> a, string k, string def = null) => a.TryGetValue(k, out var v) ? v : def;
	static int GetInt(Dictionary<string, string> a, string k, int def) => a.TryGetValue(k, out var v) ? int.Parse(v, CultureInfo.InvariantCulture) : def;

	static Dist.Weights MakeWeights(Dictionary<string, string> a)
	{
		var w = new Dist.Weights();
		var mode = Get(a, "mode", "constant");
		w.ArgsNorm = mode == "legacy" ? Dist.ArgsNormalization.Legacy : Dist.ArgsNormalization.Constant;
		w.ArgMaxCount = GetInt(a, "argmax", 8);
		var ws = Get(a, "weights");
		if (!string.IsNullOrEmpty(ws))
		{
			var p = ws.Split(',').Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToArray();
			if (p.Length != 5) throw new ArgumentException("--weights expects 5 numbers");
			w.NameW = p[0]; w.ArgsW = p[1]; w.ReturnsW = p[2]; w.ParentW = p[3]; w.NeighW = p[4];
		}
		return w;
	}

	static NeighborBagKind BagKind(Dictionary<string, string> a) => Get(a, "bags", "rich") switch
	{
		"server" => NeighborBagKind.ServerName,
		"none" => NeighborBagKind.None,
		_ => NeighborBagKind.Rich,
	};

	static List<MethodAnchor> LoadAnchors(string path)
	{
		var txt = File.ReadAllText(path);
		return JsonConvert.DeserializeObject<List<MethodAnchor>>(txt) ?? new List<MethodAnchor>();
	}

	static List<MethodAnchor> FromJArray(JArray arr) => arr.ToObject<List<MethodAnchor>>() ?? new List<MethodAnchor>();

	static string Csv(params object[] cells)
	{
		var sb = new StringBuilder();
		for (int i = 0; i < cells.Length; i++)
		{
			if (i > 0) sb.Append(',');
			var c = cells[i];
			string s = c switch
			{
				null => "",
				double dd => dd.ToString("R", CultureInfo.InvariantCulture),
				float ff => ff.ToString("R", CultureInfo.InvariantCulture),
				bool bb => bb ? "1" : "0",
				_ => Convert.ToString(c, CultureInfo.InvariantCulture),
			};
			if (s.Contains(',') || s.Contains('"') || s.Contains('\n')) s = "\"" + s.Replace("\"", "\"\"") + "\"";
			sb.Append(s);
		}
		return sb.ToString();
	}

	static void AppendCsv(string path, string header, IEnumerable<string> rows)
	{
		if (string.IsNullOrEmpty(path)) { Console.WriteLine(header); foreach (var r in rows) Console.WriteLine(r); return; }
		Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
		bool writeHeader = !File.Exists(path) || new FileInfo(path).Length == 0;
		using var sw = new StreamWriter(path, append: true, Encoding.UTF8);
		if (writeHeader) sw.WriteLine(header);
		foreach (var r in rows) sw.WriteLine(r);
	}

	static double Median(List<double> xs) { if (xs.Count == 0) return 0; var s = xs.OrderBy(x => x).ToList(); int n = s.Count; return n % 2 == 1 ? s[n / 2] : 0.5 * (s[n / 2 - 1] + s[n / 2]); }
	static double Percentile(List<double> xs, double p) { if (xs.Count == 0) return 0; var s = xs.OrderBy(x => x).ToList(); double r = p * (s.Count - 1); int lo = (int)Math.Floor(r); int hi = Math.Min(lo + 1, s.Count - 1); return s[lo] + (r - lo) * (s[hi] - s[lo]); }

	sealed class Counter { public long Value; }
	static Func<MethodAnchor, MethodAnchor, double> Counted(Dist.Weights w, Counter c) => (a, b) => { Interlocked.Increment(ref c.Value); return Dist.AnchorDistance(a, b, w); };

	static MethodAnchor CloneWith(MethodAnchor a, Action<MethodAnchor> mutate)
	{
		var q = new MethodAnchor
		{
			Id = a.Id, StartOffset = a.StartOffset, EndOffset = a.EndOffset, File = a.File,
			MethodNameNorm = a.MethodNameNorm, ParentNameNorm = a.ParentNameNorm, ReturnTypeNorm = a.ReturnTypeNorm,
			Args = (a.Args ?? new List<MethodAnchor.Arg>()).Select(x => new MethodAnchor.Arg { TypeNorm = x.TypeNorm, NameNorm = x.NameNorm }).ToList(),
			OrdinalInParent = a.OrdinalInParent, NeighborBag = a.NeighborBag,
		};
		mutate?.Invoke(q);
		return q;
	}

	// ----------------------------------------------------------------- axioms

	static int Axioms(Dictionary<string, string> a)
	{
		var anchors = CanonicalOrder.Sort(LoadAnchors(Get(a, "anchors") ?? throw new ArgumentException("--anchors")));
		AnchorsIO.BuildNeighborBags(anchors, BagKind(a), 4);
		var w = MakeWeights(a);
		int triples = GetInt(a, "triples", 100000);
		int seed = GetInt(a, "seed", 42);
		const double eps = 1e-12;
		bool V(double dab, double dbc, double dac) => dac > dab + dbc + eps;

		var rng = new Random(seed);
		// 1) structured args triples (x),(x,y),(y)
		var args = anchors.Where(x => x.Args != null).SelectMany(x => x.Args).GroupBy(x => x.TypeNorm + "" + x.NameNorm).Select(g => g.First()).ToList();
		int n1 = Math.Min(triples, 20000), v1 = 0;
		for (int t = 0; t < n1; t++)
		{
			var x = args[rng.Next(args.Count)]; var y = args[rng.Next(args.Count)];
			if (ReferenceEquals(x, y)) { t--; continue; }
			var A = new List<MethodAnchor.Arg> { x }; var B = new List<MethodAnchor.Arg> { x, y }; var C = new List<MethodAnchor.Arg> { y };
			if (V(Dist.ArgsDistance(A, B, w), Dist.ArgsDistance(B, C, w), Dist.ArgsDistance(A, C, w))) v1++;
		}
		// 2) random anchor triples, full metric
		int v2 = 0, asym = 0, range = 0;
		for (int t = 0; t < triples; t++)
		{
			var p = anchors[rng.Next(anchors.Count)]; var q = anchors[rng.Next(anchors.Count)]; var r = anchors[rng.Next(anchors.Count)];
			double dpq = Dist.AnchorDistance(p, q, w), dqr = Dist.AnchorDistance(q, r, w), dpr = Dist.AnchorDistance(p, r, w);
			if (V(dpq, dqr, dpr)) v2++;
			if (Math.Abs(dpq - Dist.AnchorDistance(q, p, w)) > 1e-9) asym++;
			if (dpq < 0 || dpq > 1 + eps) range++;
		}
		// 3) arity triples: (a, a+arg, c with that arg), full metric
		var withArgs = anchors.Where(x => x.Args != null && x.Args.Count > 0).ToList();
		var pool = withArgs.SelectMany(x => x.Args).ToList();
		int n3 = Math.Min(triples, 20000), v3 = 0;
		for (int t = 0; t < n3; t++)
		{
			var p = withArgs[rng.Next(withArgs.Count)];
			var extra = pool[rng.Next(pool.Count)];
			var q = CloneWith(p, m => m.Args.Add(extra));
			var r = CloneWith(withArgs[rng.Next(withArgs.Count)], m => m.Args = new List<MethodAnchor.Arg> { extra });
			if (V(Dist.AnchorDistance(p, q, w), Dist.AnchorDistance(q, r, w), Dist.AnchorDistance(p, r, w))) v3++;
		}
		// 4) per-component: which component breaks, if any (single-component weights)
		var comps = new Dictionary<string, Dist.Weights>
		{
			["Name"] = new Dist.Weights { NameW = 1, ArgsW = 0, ReturnsW = 0, ParentW = 0, NeighW = 0, ArgsNorm = w.ArgsNorm, ArgMaxCount = w.ArgMaxCount },
			["Args"] = new Dist.Weights { NameW = 0, ArgsW = 1, ReturnsW = 0, ParentW = 0, NeighW = 0, ArgsNorm = w.ArgsNorm, ArgMaxCount = w.ArgMaxCount },
			["Returns"] = new Dist.Weights { NameW = 0, ArgsW = 0, ReturnsW = 1, ParentW = 0, NeighW = 0, ArgsNorm = w.ArgsNorm, ArgMaxCount = w.ArgMaxCount },
			["Parent"] = new Dist.Weights { NameW = 0, ArgsW = 0, ReturnsW = 0, ParentW = 1, NeighW = 0, ArgsNorm = w.ArgsNorm, ArgMaxCount = w.ArgMaxCount },
			["Neigh"] = new Dist.Weights { NameW = 0, ArgsW = 0, ReturnsW = 0, ParentW = 0, NeighW = 1, ArgsNorm = w.ArgsNorm, ArgMaxCount = w.ArgMaxCount },
		};
		var compViol = new Dictionary<string, int>();
		foreach (var kv in comps)
		{
			int v = 0; var rr = new Random(seed + 1);
			for (int t = 0; t < n3; t++)
			{
				var p = withArgs[rr.Next(withArgs.Count)];
				var extra = pool[rr.Next(pool.Count)];
				var q = CloneWith(p, m => m.Args.Add(extra));
				var r = CloneWith(withArgs[rr.Next(withArgs.Count)], m => m.Args = new List<MethodAnchor.Arg> { extra });
				if (V(Dist.AnchorDistance(p, q, kv.Value), Dist.AnchorDistance(q, r, kv.Value), Dist.AnchorDistance(p, r, kv.Value))) v++;
			}
			compViol[kv.Key] = v;
		}

		var header = "impl,mode,bags,n_anchors,structured_n,structured_viol,random_n,random_viol,random_asym,random_out_of_range,arity_n,arity_viol,comp_Name_viol,comp_Args_viol,comp_Returns_viol,comp_Parent_viol,comp_Neigh_viol";
		var row = Csv("csharp", Get(a, "mode", "constant"), Get(a, "bags", "rich"), anchors.Count, n1, v1, triples, v2, asym, range, n3, v3,
			compViol["Name"], compViol["Args"], compViol["Returns"], compViol["Parent"], compViol["Neigh"]);
		AppendCsv(Get(a, "out"), header, new[] { row });
		Console.Error.WriteLine(row);
		return 0;
	}

	// ----------------------------------------------------------------- knn

	static int Knn(Dictionary<string, string> a)
	{
		List<MethodAnchor> oldA, newA; Dictionary<string, string> gt = null;
		var corpus = Get(a, "corpus");
		if (!string.IsNullOrEmpty(corpus))
		{
			var N = Get(a, "N") ?? throw new ArgumentException("--N");
			var jo = JObject.Parse(File.ReadAllText(corpus));
			var sub = (JObject)jo["by_N"][N];
			oldA = FromJArray((JArray)sub["old"]);
			newA = FromJArray((JArray)sub["new"]);
			gt = ((JObject)sub["gt"]).Properties().ToDictionary(p => p.Name, p => p.Value.Type == JTokenType.Null ? null : p.Value.ToString());
			// mutation labels (optional)
			var mut = ((JArray)sub["new"]).Select(x => (x["Id"]?.ToString(), x["__mutation"]?.ToString())).Where(x => x.Item1 != null).ToDictionary(x => x.Item1, x => x.Item2 ?? "");
			a["__mut"] = "1"; MutationLabels = mut;
		}
		else
		{
			oldA = LoadAnchors(Get(a, "old") ?? throw new ArgumentException("--old"));
			newA = LoadAnchors(Get(a, "new") ?? throw new ArgumentException("--new"));
			var gtPath = Get(a, "gt");
			if (!string.IsNullOrEmpty(gtPath))
				gt = JObject.Parse(File.ReadAllText(gtPath)).Properties().ToDictionary(p => p.Name, p => p.Value.Type == JTokenType.Null ? null : p.Value.ToString());
		}

		oldA = CanonicalOrder.Sort(oldA);
		var bags = BagKind(a);
		AnchorsIO.BuildNeighborBags(oldA, bags, 4);
		AnchorsIO.BuildNeighborBags(newA, bags, 4);
		var w = MakeWeights(a);
		int seed = GetInt(a, "seed", 42);
		int nq = GetInt(a, "queries", 500);
		var ks = (Get(a, "k") ?? "1").Split(',').Select(int.Parse).ToList();
		string label = Get(a, "label", "");

		var rng = new Random(seed);
		var queries = newA.Count > nq ? newA.OrderBy(_ => rng.Next()).Take(nq).ToList() : newA.ToList();

		// build (timed, counted)
		var bc = new Counter();
		var sw = Stopwatch.StartNew();
		var tree = new VPTree<MethodAnchor>(oldA, Counted(w, bc), 42);
		sw.Stop();
		double buildMs = sw.Elapsed.TotalMilliseconds; long buildCalls = bc.Value;

		// warm-up
		var qc = new Counter(); var distQ = Counted(w, qc);
		var treeQ = tree; // same tree, but count query calls via separate delegate is impossible: count via wrapper below
		// Note: tree uses its own delegate (bc). For query counting we rebuild a counting view:
		// simplest — build a second tree with the query counter (identical structure: same seed/order).
		var qtree = new VPTree<MethodAnchor>(oldA, distQ, 42);
		foreach (var q in queries.Take(5)) { qtree.KNearest(q, 1); VPTree<MethodAnchor>.BruteForceKNearest(oldA, distQ, q, 1); }

		var summaryRows = new List<string>();
		var pqRows = new List<string>();
		foreach (int k in ks)
		{
			var vpMs = new List<double>(); var bfMs = new List<double>(); var vpCalls = new List<double>();
			int mism = 0, top1Vp = 0, top1Bf = 0, matched = 0, hit5Vp = 0;
			foreach (var q in queries)
			{
				qc.Value = 0;
				sw.Restart(); var vp = qtree.KNearest(q, k); sw.Stop();
				double tVp = sw.Elapsed.TotalMilliseconds; long cVp = qc.Value;
				sw.Restart(); var bf = VPTree<MethodAnchor>.BruteForceKNearest(oldA, distQ, q, k); sw.Stop();
				double tBf = sw.Elapsed.TotalMilliseconds;
				vpMs.Add(tVp); bfMs.Add(tBf); vpCalls.Add(cVp);
				bool same = vp.Count == bf.Count && !vp.Zip(bf, (x, y) => x.Index != y.Index).Any(x => x);
				if (!same) mism++;
				string trueOld = null; bool hasGt = gt != null && gt.TryGetValue(q.Id, out trueOld) && trueOld != null;
				string vpTop = vp.Count > 0 ? oldA[vp[0].Index].Id : ""; string bfTop = bf.Count > 0 ? oldA[bf[0].Index].Id : "";
				if (hasGt)
				{
					matched++;
					if (vpTop == trueOld) top1Vp++;
					if (bfTop == trueOld) top1Bf++;
					if (vp.Take(5).Any(r => oldA[r.Index].Id == trueOld)) hit5Vp++;
				}
				string mutation = MutationLabels != null && MutationLabels.TryGetValue(q.Id, out var m) ? m : "";
				pqRows.Add(Csv(label, k, q.Id, trueOld ?? "", vpTop, bfTop, vp.Count > 0 ? vp[0].Dist : double.NaN, vp.Count > 1 ? vp[1].Dist : double.NaN, bf.Count > 0 ? bf[0].Dist : double.NaN, tVp, tBf, cVp, !same, mutation));
			}
			summaryRows.Add(Csv(label, "csharp", Get(a, "mode", "constant"), Get(a, "bags", "rich"), w.NameW, w.ArgsW, w.ReturnsW, w.ParentW, w.NeighW, w.ArgMaxCount,
				oldA.Count, newA.Count, queries.Count, k, buildMs, buildCalls, tree.BuildDepth,
				vpMs.Average(), Median(vpMs), Percentile(vpMs, 0.95), vpCalls.Average(), vpCalls.Average() / oldA.Count,
				bfMs.Average(), Median(bfMs), Percentile(bfMs, 0.95), (double)oldA.Count,
				bfMs.Sum() / Math.Max(1e-9, vpMs.Sum()), mism, matched, matched > 0 ? (double)top1Vp / matched : double.NaN, matched > 0 ? (double)top1Bf / matched : double.NaN, matched > 0 ? (double)hit5Vp / matched : double.NaN));
		}
		AppendCsv(Get(a, "out"), "label,impl,mode,bags,NameW,ArgsW,ReturnsW,ParentW,NeighW,ArgMaxCount,n_old,n_new,n_queries,k,build_ms,build_dist_calls,build_depth,vp_ms_mean,vp_ms_median,vp_ms_p95,vp_calls_mean,vp_calls_ratio,bf_ms_mean,bf_ms_median,bf_ms_p95,bf_calls,speedup,mismatches,matched,top1_vp,top1_bf,hit5_vp", summaryRows);
		var pq = Get(a, "per-query");
		if (!string.IsNullOrEmpty(pq))
			AppendCsv(pq, "label,k,query_id,true_old,vp_top1,bf_top1,vp_d1,vp_d2,bf_d1,vp_ms,bf_ms,vp_calls,mismatch,mutation", pqRows);
		foreach (var r in summaryRows) Console.Error.WriteLine(r);
		return 0;
	}

	static Dictionary<string, string> MutationLabels;

	// ----------------------------------------------------------------- knn-batch
	// Много пар (индекс, запросы, эталон) в одном процессе — для корпуса реальной истории
	// (сотни коммитов): --manifest jobs.json = [{"label":..., "index":..., "queries":..., "gt":...}, ...]
	// Остальные параметры (--k, --mode, --weights, --bags, --argmax) общие; --queries не ограничивает.

	static int KnnBatch(Dictionary<string, string> a)
	{
		var manifest = JArray.Parse(File.ReadAllText(Get(a, "manifest") ?? throw new ArgumentException("--manifest")));
		var w = MakeWeights(a);
		var bags = BagKind(a);
		var ks = (Get(a, "k") ?? "2").Split(',').Select(int.Parse).ToList();
		var outPath = Get(a, "out"); var pqPath = Get(a, "per-query");
		var summaryRows = new List<string>(); var pqRows = new List<string>();
		var sw = new Stopwatch();
		int done = 0;
		foreach (var job in manifest.OfType<JObject>())
		{
			string label = job["label"]?.ToString() ?? "";
			var index = CanonicalOrder.Sort(LoadAnchors(job["index"]!.ToString()));
			var queries = LoadAnchors(job["queries"]!.ToString());
			Dictionary<string, string> gt = null;
			var gtPath = job["gt"]?.ToString();
			if (!string.IsNullOrEmpty(gtPath))
				gt = JObject.Parse(File.ReadAllText(gtPath)).Properties().ToDictionary(p => p.Name, p => p.Value.Type == JTokenType.Null ? null : p.Value.ToString());
			if (index.Count == 0 || queries.Count == 0) { done++; continue; }
			int window = GetInt(a, "window", 4);   // окно мешка соседей (±window внутри группы файл+родитель)
			AnchorsIO.BuildNeighborBags(index, bags, window);
			AnchorsIO.BuildNeighborBags(queries, bags, window);
			var qc = new Counter(); var distQ = Counted(w, qc);
			sw.Restart(); var tree = new VPTree<MethodAnchor>(index, distQ, 42); sw.Stop();
			double buildMs = sw.Elapsed.TotalMilliseconds; long buildCalls = qc.Value;
			foreach (int k in ks)
			{
				var vpMs = new List<double>(); var bfMs = new List<double>(); var vpCalls = new List<double>();
				int mism = 0, top1 = 0, matched = 0;
				foreach (var q in queries)
				{
					qc.Value = 0;
					sw.Restart(); var vp = tree.KNearest(q, k); sw.Stop();
					double tVp = sw.Elapsed.TotalMilliseconds; long cVp = qc.Value;
					sw.Restart(); var bf = VPTree<MethodAnchor>.BruteForceKNearest(index, distQ, q, k); sw.Stop();
					vpMs.Add(tVp); bfMs.Add(sw.Elapsed.TotalMilliseconds); vpCalls.Add(cVp);
					bool same = vp.Count == bf.Count && !vp.Zip(bf, (x, y) => x.Index != y.Index).Any(x => x);
					if (!same) mism++;
					string trueId = null; bool hasGt = gt != null && gt.TryGetValue(q.Id, out trueId);
					string vpTop = vp.Count > 0 ? index[vp[0].Index].Id : "";
					if (hasGt && trueId != null) { matched++; if (vpTop == trueId) top1++; }
					pqRows.Add(Csv(label, k, q.Id, hasGt ? (trueId ?? "") : "", hasGt ? 1 : 0, vpTop, vp.Count > 0 ? vp[0].Dist : double.NaN, vp.Count > 1 ? vp[1].Dist : double.NaN,
						vp.Count > 1 ? index[vp[1].Index].Id : "", tVp, sw.Elapsed.TotalMilliseconds, cVp, !same));
				}
				summaryRows.Add(Csv(label, k, w.NameW, w.ArgsW, w.ReturnsW, w.ParentW, w.NeighW, Get(a, "mode", "constant"), Get(a, "bags", "rich"),
					index.Count, queries.Count, buildMs, buildCalls, vpMs.Average(), Median(vpMs), vpCalls.Average() / index.Count, bfMs.Average(), Median(bfMs), mism, matched, matched > 0 ? (double)top1 / matched : double.NaN));
			}
			done++;
			if (done % 50 == 0) Console.Error.WriteLine($"[knn-batch] {done}/{manifest.Count}");
		}
		AppendCsv(outPath, "label,k,NameW,ArgsW,ReturnsW,ParentW,NeighW,mode,bags,n_index,n_queries,build_ms,build_dist_calls,vp_ms_mean,vp_ms_median,vp_calls_ratio,bf_ms_mean,bf_ms_median,mismatches,matched,top1_vp", summaryRows);
		if (!string.IsNullOrEmpty(pqPath))
			AppendCsv(pqPath, "label,k,query_id,true_id,has_gt,vp_top1,vp_d1,vp_d2,vp_top2,vp_ms,bf_ms,vp_calls,mismatch", pqRows);
		Console.Error.WriteLine($"[knn-batch] done {done} jobs, {pqRows.Count} query rows");
		return 0;
	}

	// ----------------------------------------------------------------- pairs

	static int Pairs(Dictionary<string, string> a)
	{
		var anchors = CanonicalOrder.Sort(LoadAnchors(Get(a, "anchors") ?? throw new ArgumentException("--anchors")));
		AnchorsIO.BuildNeighborBags(anchors, BagKind(a), 4);
		var byId = anchors.ToDictionary(x => x.Id, x => x);
		var w = MakeWeights(a);
		var rows = new List<string>();
		foreach (var line in File.ReadLines(Get(a, "pairs") ?? throw new ArgumentException("--pairs")).Skip(1))
		{
			if (string.IsNullOrWhiteSpace(line)) continue;
			var parts = line.Split(',');
			var x = byId[parts[0]]; var y = byId[parts[1]];
			Dist.AnchorDistanceComponents(x, y, w, out var dn, out var da, out var dr, out var dp, out var dg, out var total);
			rows.Add(Csv(x.Id, y.Id, dn, da, dr, dp, dg, total));
		}
		AppendCsv(Get(a, "out"), "id_a,id_b,d_name,d_args,d_returns,d_parent,d_neigh,total", rows);
		return 0;
	}

	// ----------------------------------------------------------------- determinism

	static int Determinism(Dictionary<string, string> a)
	{
		var anchors = CanonicalOrder.Sort(LoadAnchors(Get(a, "anchors") ?? throw new ArgumentException("--anchors")));
		AnchorsIO.BuildNeighborBags(anchors, BagKind(a), 4);
		var w = MakeWeights(a);
		int repeats = GetInt(a, "repeats", 20), nq = GetInt(a, "queries", 200);
		Func<MethodAnchor, MethodAnchor, double> dist = (x, y) => Dist.AnchorDistance(x, y, w);
		var rng = new Random(GetInt(a, "seed", 42));
		var queries = new List<MethodAnchor>();
		for (int i = 0; i < nq; i++)
		{
			var src = anchors[rng.Next(anchors.Count)];
			queries.Add(CloneWith(src, m => { m.Id = "q" + i; if (i % 2 == 0) m.MethodNameNorm += "x"; else m.ReturnTypeNorm += "!"; }));
		}
		string refHash = null; int identical = 0;
		for (int rep = 0; rep < repeats; rep++)
		{
			var shuffled = anchors.OrderBy(_ => rng.Next()).ToList();
			var canon = CanonicalOrder.Sort(shuffled);
			var tree = new VPTree<MethodAnchor>(canon, dist, 42);
			var snap = tree.ToSnapshot(x => x.Id);
			var sb = new StringBuilder();
			foreach (var n in snap.Nodes) sb.Append(n.PivotId).Append('|').Append(n.Threshold.ToString("R", CultureInfo.InvariantCulture)).Append('|').Append(n.Left).Append('|').Append(n.Right).Append('\n');
			foreach (var q in queries) foreach (var r in tree.KNearest(q, 5)) sb.Append(canon[r.Index].Id).Append(':').Append(r.Dist.ToString("R", CultureInfo.InvariantCulture)).Append(';');
			var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
			if (refHash == null) refHash = hash;
			if (hash == refHash) identical++;
		}
		var row = Csv("csharp", Environment.ProcessorCount, anchors.Count, nq, repeats, identical, refHash);
		AppendCsv(Get(a, "out"), "impl,processors,n_anchors,n_queries,repeats,identical,hash", new[] { row });
		Console.Error.WriteLine(row);
		return identical == repeats ? 0 : 3;
	}
}
