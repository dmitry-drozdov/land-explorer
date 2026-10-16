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
//   assign-batch — предпроход точного ключа + k-NN + назначение один-к-одному с отказом (AssignmentRebind), e11/e12/e14; --warmup N — первая пара прогоняется N раз без записи (прогрев JIT); --warmup-all N — весь манифест N раз без записи (прогрев всех путей кода);
//                  --k 0 — кандидаты по радиусу (все якоря с d ≤ max(reject), VPTree.WithinRadius) вместо k ближайших; --verify-radius N — при --k 0 сверить первые N запросов каждой пары с перебором (e15)
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
				case "save-index": return SaveIndex(args);
				case "assign-batch": return AssignBatch(args);
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

	// save-index: сохранение индекса штатными средствами библиотеки — якоря с мешками соседей (AnchorsIO.SaveJson)
	// и снимок VP-дерева (VpTreeStorage.SaveJson, как Rebinder.ShapShot); затем восстановление из обоих файлов
	// (AnchorsIO.LoadJson + VPTree.FromSnapshot) и сверка k-NN восстановленного дерева с построенным на выборке запросов.
	// Для сравнения объёма хранения с файлом разметки панели (CfBench --save-markup), markup/experiments/e13_storage_size.
	static int SaveIndex(Dictionary<string, string> a)
	{
		var anchors = CanonicalOrder.Sort(LoadAnchors(Get(a, "anchors") ?? throw new ArgumentException("--anchors")));
		var parents = Get(a, "parents");
		if (!string.IsNullOrEmpty(parents))
		{
			var set = new HashSet<string>(parents.Split(',').Select(x => MethodAnchor.NormalizeName(x.Trim())), StringComparer.Ordinal);
			anchors = anchors.Where(x => set.Contains(x.ParentNameNorm ?? "")).ToList();
		}
		if (anchors.Count == 0) throw new InvalidOperationException("no anchors");
		var w = MakeWeights(a);
		var bags = BagKind(a);
		int window = GetInt(a, "window", 4);
		string label = Get(a, "label", "");
		string outDir = Get(a, "out-dir") ?? throw new ArgumentException("--out-dir");
		Directory.CreateDirectory(outDir);
		Func<MethodAnchor, MethodAnchor, double> dist = (x, y) => Dist.AnchorDistance(x, y, w);

		int repeats = Math.Max(1, GetInt(a, "repeats", 1));
		var bagsMsList = new List<double>(); var treeMsList = new List<double>();
		var saveAnchorsMsList = new List<double>(); var saveTreeMsList = new List<double>(); var loadMsList = new List<double>();
		var anchorsPath = Path.Combine(outDir, "anchors.json");
		var treePath = Path.Combine(outDir, "vp_index.json");
		var sw = new Stopwatch();
		VPTree<MethodAnchor> tree = null;
		VpTreeSnapshot snap = null;
		for (int rep = 0; rep < repeats; rep++)
		{
			sw.Restart();
			AnchorsIO.BuildNeighborBags(anchors, bags, window);
			bagsMsList.Add(sw.Elapsed.TotalMilliseconds);
			sw.Restart();
			tree = new VPTree<MethodAnchor>(anchors, dist, 42);
			treeMsList.Add(sw.Elapsed.TotalMilliseconds);
			sw.Restart();
			AnchorsIO.SaveJson(anchorsPath, anchors);
			saveAnchorsMsList.Add(sw.Elapsed.TotalMilliseconds);
			snap = tree.ToSnapshot(x => x.Id, $"weights={w.NameW},{w.ArgsW},{w.ReturnsW},{w.ParentW},{w.NeighW};bags={bags};window={window};seed=42");
			sw.Restart();
			VpTreeStorage.SaveJson(treePath, snap);
			saveTreeMsList.Add(sw.Elapsed.TotalMilliseconds);
			if (rep + 1 < repeats)
			{
				sw.Restart();
				var l = AnchorsIO.LoadJson(anchorsPath);
				var sl = VpTreeStorage.LoadJson(treePath);
				var ix = new Dictionary<string, int>(l.Count, StringComparer.Ordinal);
				for (int i = 0; i < l.Count; i++) ix[l[i].Id] = i;
				_ = VPTree<MethodAnchor>.FromSnapshot(sl, l, id => ix.TryGetValue(id, out var i) ? i : -1, dist);
				loadMsList.Add(sw.Elapsed.TotalMilliseconds);
			}
		}
		double bagsMs = Median(bagsMsList), treeMs = Median(treeMsList), saveAnchorsMs = Median(saveAnchorsMsList), saveTreeMs = Median(saveTreeMsList);

		// --also-nobags: тот же список якорей без мешков соседей (вариант C хранения: мешки строятся при загрузке)
		long nobagsBytes = -1;
		if (a.ContainsKey("also-nobags"))
		{
			var stripped = anchors.Select(x => new MethodAnchor
			{
				Id = x.Id, StartOffset = x.StartOffset, EndOffset = x.EndOffset, MethodNameNorm = x.MethodNameNorm,
				ParentNameNorm = x.ParentNameNorm, ReturnTypeNorm = x.ReturnTypeNorm, Args = x.Args, OrdinalInParent = x.OrdinalInParent,
				File = x.File, NeighborBag = null,
			}).ToList();
			var nobagsPath = Path.Combine(outDir, "anchors_nobags.json");
			AnchorsIO.SaveJson(nobagsPath, stripped);
			nobagsBytes = new FileInfo(nobagsPath).Length;
		}

		// восстановление: якоря (мешки уже в файле) + дерево из снимка
		sw.Restart();
		var loaded = AnchorsIO.LoadJson(anchorsPath);
		var snapLoaded = VpTreeStorage.LoadJson(treePath);
		var idx = new Dictionary<string, int>(loaded.Count, StringComparer.Ordinal);
		for (int i = 0; i < loaded.Count; i++) idx[loaded[i].Id] = i;
		var restored = VPTree<MethodAnchor>.FromSnapshot(snapLoaded, loaded, id => idx.TryGetValue(id, out var i) ? i : -1, dist);
		loadMsList.Add(sw.Elapsed.TotalMilliseconds);
		double loadMs = Median(loadMsList);

		int nq = Math.Min(GetInt(a, "verify", 200), anchors.Count);
		var rng = new Random(42);
		var order = Enumerable.Range(0, anchors.Count).OrderBy(_ => rng.Next()).Take(nq).ToList();
		int mismatch = 0;
		foreach (int qi in order)
		{
			var r1 = tree.KNearest(anchors[qi], 2);
			var r2 = restored.KNearest(loaded[qi], 2);
			bool same = r1.Count == r2.Count;
			for (int j = 0; same && j < r1.Count; j++)
				same = anchors[r1[j].Index].Id == loaded[r2[j].Index].Id && r1[j].Dist == r2[j].Dist;
			if (!same) mismatch++;
		}
		AppendCsv(Get(a, "out"), "label,n,bags_ms,tree_ms,anchors_bytes,tree_bytes,save_anchors_ms,save_tree_ms,load_ms,tree_nodes,verify_queries,verify_mismatch,anchors_nobags_bytes,repeats",
			new[] { Csv(label, anchors.Count, bagsMs, treeMs, new FileInfo(anchorsPath).Length, new FileInfo(treePath).Length, saveAnchorsMs, saveTreeMs, loadMs, snap.Nodes.Count, nq, mismatch, nobagsBytes, repeats) });
		Console.Error.WriteLine($"[save-index] {label}: n={anchors.Count} anchors={new FileInfo(anchorsPath).Length} bytes, tree={new FileInfo(treePath).Length} bytes, restore {loadMs:F0} ms, mismatches {mismatch}/{nq}");
		return mismatch == 0 ? 0 : 1;
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
  VPTreeBench save-index --anchors a.json --out-dir DIR [--label text] [--parents Query,Mutation] [--bags ...] [--weights ...] [--window 4] [--verify 200] [--also-nobags] [--repeats 1] --out sizes.csv
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
	static double GetDouble(Dictionary<string, string> a, string k, double def) => a.TryGetValue(k, out var v) ? double.Parse(v, CultureInfo.InvariantCulture) : def;

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
			if (p.Length != 5 && p.Length != 6) throw new ArgumentException("--weights expects 5 numbers (6 with the body, e24)");
			w.NameW = p[0]; w.ArgsW = p[1]; w.ReturnsW = p[2]; w.ParentW = p[3]; w.NeighW = p[4];
			if (p.Length == 6) w.BodyW = p[5];
		}
		return w;
	}

	static NeighborBagKind BagKind(Dictionary<string, string> a) => Get(a, "bags", "rich") switch
	{
		"server" => NeighborBagKind.ServerName,
		"none" => NeighborBagKind.None,
		_ => NeighborBagKind.Rich,
	};

	/// Порядок точек, как при сканировании сервера: файлы по возрастанию полного пути (разделитель «\»), внутри файла — по смещению.
	static List<MethodAnchor> ScanOrder(List<MethodAnchor> anchors) => anchors
		.OrderBy(x => (x.File ?? "").Replace('/', '\\'), StringComparer.Ordinal).ThenBy(x => x.StartOffset).ToList();

	/// Ключ слоя 1 сервера (LandService.BuildStitchExactKey): язык и вид одинаковы для всех точек стенда, путь — в нижнем регистре с «/»;
	/// все точки стенда вызываемые (поля GraphQL, функции Go), поэтому сигнатура в ключе есть всегда.
	static string StitchKey(MethodAnchor x) =>
		StitchLayer1.Key("", "", (x.File ?? "").Replace('\\', '/').ToLowerInvariant(), x.ParentNameRaw ?? "", x.MethodNameNorm ?? "",
			AnchorKeys.Signature((x.Args ?? new List<MethodAnchor.Arg>()).Select(y => (y.TypeNorm ?? "", y.NameNorm ?? "")), x.ReturnTypeNorm ?? ""));

	/// Ключ слоя 1б сервера (LandService.BuildStitchGlobalKey): без пути файла, с сигнатурой.
	static string StitchGlobalKey(MethodAnchor x) =>
		StitchLayer1.GlobalKey("", "", x.ParentNameRaw ?? "", x.MethodNameNorm ?? "",
			AnchorKeys.Signature((x.Args ?? new List<MethodAnchor.Arg>()).Select(y => (y.TypeNorm ?? "", y.NameNorm ?? "")), x.ReturnTypeNorm ?? ""));

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
			OrdinalInParent = a.OrdinalInParent, NeighborBag = a.NeighborBag, BodySig = a.BodySig,
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

		// 5) отпечаток тела (e24): компонента тела отдельно и полная метрика с её весом на «близких» тройках — q отличается от p
		// в нескольких значениях отпечатка, r от q ещё в нескольких; с вероятностью 0,1 у каждой точки тела нет (null)
		var withBody = anchors.Where(x => x.BodySig != null).ToList();
		int n5 = withBody.Count > 0 ? Math.Min(triples, 50000) : 0, v5body = 0, v5full = 0;
		var bodyOnly = new Dist.Weights { NameW = 0, ArgsW = 0, ReturnsW = 0, ParentW = 0, NeighW = 0, BodyW = 1, ArgsNorm = w.ArgsNorm, ArgMaxCount = w.ArgMaxCount };
		var r5 = new Random(seed + 2);
		byte[]? Perturb(byte[]? sig)
		{
			if (sig == null || r5.NextDouble() < 0.1) return r5.NextDouble() < 0.5 ? null : sig;
			var c = (byte[])sig.Clone();
			for (int k = r5.Next(0, 9); k > 0; k--) { var s = r5.Next(BodySignature.Slots); c[2 * s] ^= (byte)(1 + r5.Next(255)); }
			return c;
		}
		for (int t = 0; t < n5; t++)
		{
			var p = withBody[r5.Next(withBody.Count)];
			var q = CloneWith(p, m => m.BodySig = Perturb(p.BodySig));
			var r = CloneWith(r5.NextDouble() < 0.5 ? p : withBody[r5.Next(withBody.Count)], m => m.BodySig = Perturb(q.BodySig));
			if (V(Dist.AnchorDistance(p, q, bodyOnly), Dist.AnchorDistance(q, r, bodyOnly), Dist.AnchorDistance(p, r, bodyOnly))) v5body++;
			if (V(Dist.AnchorDistance(p, q, w), Dist.AnchorDistance(q, r, w), Dist.AnchorDistance(p, r, w))) v5full++;
		}

		var header = "impl,mode,bags,n_anchors,structured_n,structured_viol,random_n,random_viol,random_asym,random_out_of_range,arity_n,arity_viol,comp_Name_viol,comp_Args_viol,comp_Returns_viol,comp_Parent_viol,comp_Neigh_viol,body_w,body_n,comp_Body_viol,body_full_viol";
		var row = Csv("csharp", Get(a, "mode", "constant"), Get(a, "bags", "rich"), anchors.Count, n1, v1, triples, v2, asym, range, n3, v3,
			compViol["Name"], compViol["Args"], compViol["Returns"], compViol["Parent"], compViol["Neigh"], w.BodyW, n5, v5body, v5full);
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


	// assign-batch: полный шаг перепривязки VP-дерева на парах манифеста knn-batch — предпроход точного ключа
	// (AnchorKeys.CanonExact, один-к-одному: ключ единственный и среди запросов, и в индексе), k ближайших по дереву,
	// построенному над незанятыми якорями новой версии, затем назначение один-к-одному с отказом
	// (VPTreeLib.AssignmentRebind — тот же код, что в сервере) с одной или несколькими ценами отказа (--reject auto|0.30,...).
	// Выход: по парам (время шагов, компоненты, статусы) и по запросам (решение, Δ, отношение, компонента, k ближайших
	// с расстояниями — для сверки с Python-реализацией и поточечного правила на тех же списках).
	static int AssignBatch(Dictionary<string, string> a)
	{
		var manifest = JArray.Parse(File.ReadAllText(Get(a, "manifest") ?? throw new ArgumentException("--manifest")));
		var w = MakeWeights(a);
		var bags = BagKind(a);
		int window = GetInt(a, "window", 4);
		int k = GetInt(a, "k", 3);
		double tau = GetDouble(a, "tau", 0.30), margin = GetDouble(a, "margin", 0.30);
		int enumLimit = GetInt(a, "enum-limit", 10), kBest = GetInt(a, "kbest", 3);
		// --prepass 1 (по умолчанию) — ключ AnchorKeys.CanonExact без файла, только единственные совпадения (серии e11–e20);
		// --prepass server — слой 1 сервера плагина (VPTreeLib.StitchLayer1: ключ с путём файла, ближайшее смещение), порядок
		// точек как при сканировании сервера, слой 2 с параметрами сервера (k ≥ 2, KBest = 0) — серия e21_server_stitch_mode;
		// --prepass 0 — без предпрохода
		var prepassMode = (Get(a, "prepass") ?? "1").Trim().ToLowerInvariant();
		bool serverMode = prepassMode == "server";
		bool prepass = prepassMode != "0";
		var rejects = (Get(a, "reject") ?? "auto").Split(',')
			.Select(x => x.Trim() == "auto" ? tau / (1.0 - margin) : double.Parse(x, CultureInfo.InvariantCulture)).ToList();
		var outPath = Get(a, "out"); var pqPath = Get(a, "per-query");
		var pairRows = new List<string>(); var pqRows = new List<string>();
		Func<MethodAnchor, MethodAnchor, double> dist = (x, y) => Dist.AnchorDistance(x, y, w);
		var sw = new Stopwatch();
		int done = 0;
		int warmup = GetInt(a, "warmup", 0);   // прогрев JIT: первая пара решается warmup раз без записи
		int warmupAll = GetInt(a, "warmup-all", 0);   // прогрев всех путей кода: весь манифест проходится warmup-all раз без записи
		double radius = rejects.Max();          // --k 0: кандидаты — все якоря с d ≤ radius (при нескольких ценах отказа списки полны для каждой)
		int verifyRadius = GetInt(a, "verify-radius", 0);
		long verifyChecked = 0, verifyMismatch = 0;
		var jobList = manifest.OfType<JObject>().ToList();
		void RunJob(JObject job, bool record)
		{
			string label = job["label"]?.ToString() ?? "";
			var index = serverMode ? ScanOrder(LoadAnchors(job["index"]!.ToString())) : CanonicalOrder.Sort(LoadAnchors(job["index"]!.ToString()));
			var queries = serverMode ? ScanOrder(LoadAnchors(job["queries"]!.ToString())) : LoadAnchors(job["queries"]!.ToString());
			Dictionary<string, string> gt = null;
			var gtPath = job["gt"]?.ToString();
			if (!string.IsNullOrEmpty(gtPath))
				gt = JObject.Parse(File.ReadAllText(gtPath)).Properties().ToDictionary(p => p.Name, p => p.Value.Type == JTokenType.Null ? null : p.Value.ToString());
			if (index.Count == 0 || queries.Count == 0) { if (record) done++; return; }
			AnchorsIO.BuildNeighborBags(index, bags, window);
			AnchorsIO.BuildNeighborBags(queries, bags, window);

			// предпроход точного ключа. --prepass 1: ключ AnchorKeys.CanonExact (parent|method|args>return) по всей версии, один-к-одному;
			// так считались e11–e20, это НЕ первый слой сервера (сервер ExactHashRebinder не вызывает, e21). --prepass server: слой 1 сервера
			// (VPTreeLib.StitchLayer1: путь файла + сигнатура, ближайшее смещение; затем слой 1б без пути, только единственное совпадение)
			sw.Restart();
			var exact = new Dictionary<int, int>();      // позиция запроса → позиция в индексе
			var claimed = new HashSet<int>();
			if (serverMode)
			{
				var m = StitchLayer1.Match(queries.Select(StitchKey).ToList(), queries.Select(x => x.StartOffset).ToList(),
					index.Select(StitchKey).ToList(), index.Select(x => x.StartOffset).ToList(),
					queries.Select(StitchGlobalKey).ToList(), index.Select(StitchGlobalKey).ToList());
				for (int q = 0; q < m.Length; q++)
					if (m[q] >= 0) { exact[q] = m[q]; claimed.Add(m[q]); }
			}
			else if (prepass)
			{
				var byKeyIndex = new Dictionary<string, List<int>>(StringComparer.Ordinal);
				for (int i = 0; i < index.Count; i++)
				{
					var key = AnchorKeys.CanonExact(index[i]);
					if (!byKeyIndex.TryGetValue(key, out var l)) byKeyIndex[key] = l = new List<int>();
					l.Add(i);
				}
				var byKeyQuery = new Dictionary<string, List<int>>(StringComparer.Ordinal);
				for (int q = 0; q < queries.Count; q++)
				{
					var key = AnchorKeys.CanonExact(queries[q]);
					if (!byKeyQuery.TryGetValue(key, out var l)) byKeyQuery[key] = l = new List<int>();
					l.Add(q);
				}
				foreach (var kv in byKeyQuery)
					if (kv.Value.Count == 1 && byKeyIndex.TryGetValue(kv.Key, out var l) && l.Count == 1)
					{
						exact[kv.Value[0]] = l[0];
						claimed.Add(l[0]);
					}
			}
			sw.Stop();
			double prepassMs = sw.Elapsed.TotalMilliseconds;

			var rest = new List<MethodAnchor>(index.Count);
			for (int i = 0; i < index.Count; i++) if (!claimed.Contains(i)) rest.Add(index[i]);
			sw.Restart();
			var tree = rest.Count > 0 ? new VPTree<MethodAnchor>(rest, dist, 42) : null;
			sw.Stop();
			double treeMs = sw.Elapsed.TotalMilliseconds;

			var pts = new List<AssignmentRebind.Point>();
			var topk = new Dictionary<int, List<VPTree<MethodAnchor>.KNNResult>>();
			sw.Restart();
			// сервер обходит несопоставленные старые точки по возрастанию Id и запрашивает не меньше двух соседей
			var layer2Order = Enumerable.Range(0, queries.Count).Where(q => !exact.ContainsKey(q)).ToList();
			if (serverMode) layer2Order = layer2Order.OrderBy(q => queries[q].Id ?? "", StringComparer.Ordinal).ToList();
			int kQuery = serverMode ? Math.Max(2, k) : k;
			foreach (int q in layer2Order)
			{
				var knn = tree == null ? new List<VPTree<MethodAnchor>.KNNResult>()
						: (kQuery > 0 ? tree.KNearest(queries[q], kQuery) : tree.WithinRadius(queries[q], radius));
				topk[q] = knn;
				var pt = new AssignmentRebind.Point { Index = q };
				foreach (var r in knn) pt.Candidates.Add(new AssignmentRebind.Candidate(r.Index, r.Dist));
				pts.Add(pt);
			}
			sw.Stop();
			double knnMs = sw.Elapsed.TotalMilliseconds;
			long candTotal = topk.Values.Sum(l => (long)l.Count);
			int vChecked = 0, vMismatch = 0;
			if (k == 0 && verifyRadius > 0 && tree != null && record)
			{
				foreach (var kv in topk)
				{
					if (vChecked >= verifyRadius) break;
					var bf = VPTree<MethodAnchor>.BruteForceWithinRadius(rest, dist, queries[kv.Key], radius);
					bool same = bf.Count == kv.Value.Count && bf.Zip(kv.Value, (x, y) => x.Index == y.Index).All(z => z);
					vChecked++;
					if (!same) vMismatch++;
				}
				verifyChecked += vChecked; verifyMismatch += vMismatch;
			}

			foreach (double reject in rejects)
			{
				var opt = new AssignmentRebind.Options { Tau = tau, Margin = margin, RejectCost = reject, EnumerateLimit = enumLimit, KBest = serverMode ? 0 : kBest };
				sw.Restart();
				var result = AssignmentRebind.Solve(pts, opt);
				sw.Stop();
				double assignMs = sw.Elapsed.TotalMilliseconds;
				var decByQ = result.Decisions.ToDictionary(d => d.Point, d => d);
				string rs = reject.ToString("0.####", CultureInfo.InvariantCulture);
				int nAcc = 0, nAmb = 0, nLost = 0;
				for (int q = 0; q < queries.Count; q++)
				{
					string trueId = null; bool hasGt = gt != null && gt.TryGetValue(queries[q].Id, out trueId);
					string trueCell = hasGt ? (trueId ?? "") : "";
					if (exact.TryGetValue(q, out int ip))
					{
						nAcc++;
						if (record) pqRows.Add(Csv(label, rs, queries[q].Id, trueCell, hasGt, "exact", "accepted", index[ip].Id, 0.0, double.PositiveInfinity, 1.0, -1, 1, "", "", 0));
						continue;
					}
					var d = decByQ[q];
					string status;
					switch (d.Status)
					{
						case AssignmentRebind.Status.Accepted: status = "accepted"; nAcc++; break;
						case AssignmentRebind.Status.Ambiguous: status = "ambiguous"; nAmb++; break;
						default: status = "lost"; nLost++; break;
					}
					var knn = topk[q];
					string ids = string.Join(";", knn.Select(r => rest[r.Index].Id));
					string ds = string.Join(";", knn.Select(r => r.Dist.ToString("R", CultureInfo.InvariantCulture)));
					int nWithin = knn.Count(r => r.Dist <= reject);
					if (record) pqRows.Add(Csv(label, rs, queries[q].Id, trueCell, hasGt, "assign", status, d.Candidate >= 0 ? rest[d.Candidate].Id : "",
						d.Dist, d.Delta, d.Ratio, d.Component, d.ComponentSize, ids, ds, nWithin));
				}
				var comps = result.Components.Where(c => c.Points.Count >= 2).ToList();
				if (record) pairRows.Add(Csv(label, rs, index.Count, queries.Count, exact.Count, rest.Count, prepassMs, treeMs, knnMs, assignMs,
					comps.Count, comps.Count > 0 ? comps.Max(c => c.Points.Count) : 0, result.EnumeratedComponents, result.LapComponents, nAcc, nAmb, nLost,
					k, candTotal, vChecked, vMismatch));
			}
			if (!record) return;
			done++;
			if (done % 50 == 0) Console.Error.WriteLine($"[assign-batch] {done}/{manifest.Count}");
		}
		if (warmup > 0 && jobList.Count > 0)
			for (int wi = 0; wi < warmup; wi++) RunJob(jobList[0], false);
		// Первая пара манифеста может не дойти до назначения (всё снимает предпроход), и тогда код назначения компилируется JIT
		// на первой паре, которая до него дошла, — её время завышено в десятки раз (e17: 14,7 мс против 0,08 мс). Полный проход
		// без записи прогревает все пути кода.
		for (int wi = 0; wi < warmupAll; wi++)
			foreach (var job in jobList) RunJob(job, false);
		foreach (var job in jobList) RunJob(job, true);
		AppendCsv(outPath, "label,reject,n_index,n_queries,n_exact,n_index_rest,prepass_ms,tree_ms,knn_ms,assign_ms,n_components,max_component,n_enumerated,n_lap,n_accepted,n_ambiguous,n_lost,k,n_cand_total,verify_checked,verify_mismatch", pairRows);
		if (!string.IsNullOrEmpty(pqPath))
			AppendCsv(pqPath, "label,reject,query_id,true_id,has_gt,source,status,cand_id,dist,delta,ratio,component,component_size,topk_ids,topk_dists,n_within", pqRows);
		Console.Error.WriteLine($"[assign-batch] done {done} jobs, {pqRows.Count} query rows, k={k}{(k == 0 ? $" (radius {radius.ToString("0.####", CultureInfo.InvariantCulture)})" : "")}, rejects={string.Join(",", rejects.Select(r => r.ToString("0.####", CultureInfo.InvariantCulture)))}"
			+ (verifyRadius > 0 && k == 0 ? $", radius verify: {verifyChecked} checked, {verifyMismatch} mismatches" : ""));
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
