using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace VPTree
{
	/// <summary>
	/// Назначение один-к-одному с отказом — завершающий шаг перепривязки после поиска кандидатов по VP-дереву.
	///
	/// Постановка — линейная задача о назначениях (Kuhn, 1955): слева старые точки, справа сущности новой версии,
	/// ребро — кандидат из k ближайших с расстоянием d ≤ R, вес ребра d; точка может остаться без привязки за цену R
	/// (фиктивный кандидат). Задача распадается по компонентам связности двудольного графа; в компоненте берётся
	/// назначение минимальной цены: перебор при числе точек ≤ EnumerateLimit, венгерский метод (Hungarian) иначе.
	///
	/// Приём — критерий отношения расстояний (Lowe, 2004), перенесённый со второго соседа точки на второе назначение
	/// компоненты: p → c принимается, если лучшее назначение с запрещённым ребром p → c (подзадача ранжирования
	/// назначений Murty, 1968) дороже оптимума на Δ и Δ / (d + Δ) ≥ m. k лучших назначений компоненты (Variants) —
	/// для показа пользователю спорных компонент целиком.
	///
	/// Цена отказа по умолчанию R = τ / (1 − m). Тогда для точки без конкурентов правило совпадает с поточечным
	/// (d₁ ≤ τ и (d₂ − d₁) / d₂ ≥ m), а условие d ≤ τ следует из критерия: Δ ≤ R − d. Рёбра с d > R ни на что не влияют.
	///
	/// Детерминизм: точки и кандидаты упорядочиваются по индексам; перебор и венгерский метод не зависят от порядка входа.
	/// Реализация общая для сервера плагина (LandService, сшивка якорей) и стенда экспериментов (VPTreeBench assign-batch).
	/// </summary>
	public static class AssignmentRebind
	{
		/// <summary>«Запрещённое» ребро в матрице стоимостей венгерского метода.</summary>
		public const double Big = 1e6;

		public sealed class Candidate
		{
			public int Index;
			public double Dist;
			public Candidate() { }
			public Candidate(int index, double dist) { Index = index; Dist = dist; }
		}

		public sealed class Point
		{
			public int Index;
			public List<Candidate> Candidates = new List<Candidate>();
			public Point() { }
			public Point(int index, params (int Cand, double Dist)[] cands)
			{
				Index = index;
				foreach (var (c, d) in cands) Candidates.Add(new Candidate(c, d));
			}
		}

		public enum Status { Accepted, Ambiguous, Lost }

		public sealed class Decision
		{
			public int Point;                       // Point.Index
			public Status Status;
			public int Candidate = -1;              // привязка в оптимальном назначении (и для Ambiguous); -1 — без привязки
			public double Dist = double.NaN;        // расстояние до Candidate
			public double Delta = double.NaN;       // цена запрета ребра Point → Candidate; +∞, если альтернативы нет
			public double Ratio = double.NaN;       // Δ / (d + Δ)
			public int Component;                   // номер компоненты в Result.Components
			public int ComponentSize;
			public string Reason = "";
		}

		/// <summary>Одно назначение компоненты: Candidates[i] — кандидат точки Component.Points[i], -1 — без привязки.</summary>
		public sealed class Variant
		{
			public double Cost;
			public int[] Candidates = Array.Empty<int>();
		}

		public sealed class Component
		{
			public int Id;
			public List<int> Points = new List<int>();   // Point.Index в порядке возрастания
			public int CandidateCount;
			public double OptCost;
			public bool Enumerated;
			public List<Variant> Variants = new List<Variant>();   // k лучших по возрастанию цены (Variants[0] — оптимум)
		}

		public sealed class Options
		{
			public double Tau = 0.30;
			public double Margin = 0.30;
			/// <summary>Цена точки без привязки; NaN — по умолчанию τ / (1 − m).</summary>
			public double RejectCost = double.NaN;
			/// <summary>Компоненты не больше этого числа точек решаются перебором, остальные — венгерским методом.</summary>
			public int EnumerateLimit = 10;
			/// <summary>Сколько лучших назначений компоненты возвращать (0 — не считать).</summary>
			public int KBest = 3;

			public double ResolvedRejectCost => double.IsNaN(RejectCost) ? Tau / (1.0 - Margin) : RejectCost;
		}

		public sealed class Result
		{
			public double RejectCost;
			public List<Decision> Decisions = new List<Decision>();       // в порядке входа
			public List<Component> Components = new List<Component>();
			public int EnumeratedComponents;
			public int LapComponents;
		}

		public static Result Solve(IReadOnlyList<Point> points, Options? options = null)
		{
			var opt = options ?? new Options();
			if (opt.Margin < 0.0 || opt.Margin >= 1.0) throw new ArgumentException("margin must be in [0, 1)");
			if (opt.Tau < 0.0) throw new ArgumentException("tau must be non-negative");
			double R = opt.ResolvedRejectCost;
			if (!(R >= opt.Tau)) throw new ArgumentException("reject cost must be >= tau");
			var res = new Result { RejectCost = R };
			int n = points.Count;
			if (n == 0) return res;

			// порядок точек — по Point.Index, чтобы результат не зависел от порядка входа
			var order = Enumerable.Range(0, n).OrderBy(i => points[i].Index).ThenBy(i => i).ToArray();
			var edges = new List<(int Cand, double Dist)>[n];
			var withinTau = new bool[n];
			for (int i = 0; i < n; i++)
			{
				var best = new Dictionary<int, double>();
				foreach (var c in points[i].Candidates ?? new List<Candidate>())
				{
					if (c == null || double.IsNaN(c.Dist)) continue;
					if (c.Dist <= opt.Tau) withinTau[i] = true;
					if (c.Dist > R) continue;
					if (!best.TryGetValue(c.Index, out var d) || c.Dist < d) best[c.Index] = c.Dist;
				}
				edges[i] = best.Select(kv => (kv.Key, kv.Value)).OrderBy(e => e.Value).ThenBy(e => e.Key).ToList();
			}

			// компоненты связности: точки, делящие кандидата
			var parent = Enumerable.Range(0, n).ToArray();
			int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
			var firstByCand = new Dictionary<int, int>();
			foreach (int i in order)
				foreach (var (c, _) in edges[i])
				{
					if (firstByCand.TryGetValue(c, out int j)) { int a = Find(i), b = Find(j); if (a != b) parent[b] = a; }
					else firstByCand[c] = i;
				}
			var groups = new Dictionary<int, List<int>>();
			foreach (int i in order)
			{
				int r = Find(i);
				if (!groups.TryGetValue(r, out var g)) groups[r] = g = new List<int>();
				g.Add(i);
			}

			var decisions = new Decision[n];
			foreach (var members in groups.Values.OrderBy(g => points[g[0]].Index).ThenBy(g => g[0]))
			{
				var comp = new Component { Id = res.Components.Count, Points = members.Select(i => points[i].Index).ToList() };
				res.Components.Add(comp);
				var cols = members.SelectMany(i => edges[i].Select(e => e.Cand)).Distinct().OrderBy(c => c).ToList();
				comp.CandidateCount = cols.Count;
				var colOf = new Dictionary<int, int>();
				for (int k = 0; k < cols.Count; k++) colOf[cols[k]] = k;
				int m = members.Count;
				var memberEdges = members.Select(i => edges[i].Select(e => (Col: colOf[e.Cand], e.Dist)).ToList()).ToList();

				double optCost; int[] bestChoice; double[] altCost; List<Variant> variants;
				if (cols.Count == 0)
				{
					optCost = m * R;
					bestChoice = Enumerable.Repeat(-1, m).ToArray();
					altCost = Enumerable.Repeat(double.PositiveInfinity, m).ToArray();
					variants = opt.KBest > 0 ? new List<Variant> { new Variant { Cost = optCost, Candidates = Enumerable.Repeat(-1, m).ToArray() } } : new List<Variant>();
					comp.Enumerated = true;
				}
				else if (m <= opt.EnumerateLimit)
				{
					EnumerateComponent(memberEdges, R, opt.KBest, out optCost, out bestChoice, out altCost, out variants);
					comp.Enumerated = true;
					res.EnumeratedComponents++;
				}
				else
				{
					LapComponent(memberEdges, cols.Count, R, opt.KBest, out optCost, out bestChoice, out altCost, out variants);
					res.LapComponents++;
				}
				comp.OptCost = optCost;
				comp.Variants = variants
					.Select(v => new Variant { Cost = v.Cost, Candidates = v.Candidates.Select(col => col < 0 ? -1 : cols[col]).ToArray() })
					.ToList();

				for (int k = 0; k < m; k++)
				{
					int i = members[k];
					var dec = new Decision { Point = points[i].Index, Component = comp.Id, ComponentSize = m };
					if (bestChoice[k] >= 0)
					{
						int col = bestChoice[k];
						dec.Candidate = cols[col];
						dec.Dist = memberEdges[k].First(e => e.Col == col).Dist;
						double delta = double.IsPositiveInfinity(altCost[k]) ? double.PositiveInfinity : altCost[k] - optCost;
						if (delta < 1e-9) delta = 0.0;   // ничья с точностью до округления — не отрыв (при d = 0 иначе Δ/(d + Δ) = 1)
						dec.Delta = delta;
						dec.Ratio = double.IsPositiveInfinity(delta) ? 1.0 : (dec.Dist + delta > 0 ? delta / (dec.Dist + delta) : 0.0);
						bool close = dec.Dist <= opt.Tau + 1e-12;
						if (close && dec.Ratio >= opt.Margin)
						{
							dec.Status = Status.Accepted;
							dec.Reason = string.Format(CultureInfo.InvariantCulture, "d={0:F4} delta={1:F4} ratio={2:F3}", dec.Dist, dec.Delta, dec.Ratio);
						}
						else
						{
							dec.Status = withinTau[i] ? Status.Ambiguous : Status.Lost;
							dec.Reason = !close
								? string.Format(CultureInfo.InvariantCulture, "d={0:F4} > tau={1:F2}", dec.Dist, opt.Tau)
								: string.Format(CultureInfo.InvariantCulture, "ratio={0:F3} < {1:F2} (d={2:F4}, delta={3:F4})", dec.Ratio, opt.Margin, dec.Dist, dec.Delta);
						}
					}
					else
					{
						dec.Status = withinTau[i] ? Status.Ambiguous : Status.Lost;
						dec.Reason = edges[i].Count == 0 ? "no candidates within reject cost" : "unbound in the optimal assignment";
					}
					decisions[i] = dec;
				}
			}
			res.Decisions = decisions.ToList();
			return res;
		}

		// ---------------------------------------------------------------- перебор (компоненты ≤ EnumerateLimit точек)

		/// <summary>
		/// Два прохода по всем допустимым расстановкам компоненты: 1) оптимум (первый минимальный в порядке обхода:
		/// рёбра по возрастанию (d, столбец), затем «без привязки»); 2) для каждой привязанной точки — минимальная цена
		/// расстановки, где она привязана иначе или никуда (Δ), и k лучших расстановок. Отсечение по накопленной цене.
		/// </summary>
		private static void EnumerateComponent(List<List<(int Col, double Dist)>> e, double R, int kBest,
			out double optCost, out int[] bestChoice, out double[] altCost, out List<Variant> variants)
		{
			int m = e.Count;
			int nCols = e.SelectMany(x => x).Select(x => x.Col).DefaultIfEmpty(-1).Max() + 1;
			var used = new bool[nCols];
			var choice = new int[m];
			double best = double.PositiveInfinity;
			var bestArr = new int[m];

			void Dfs1(int k, double acc)
			{
				if (acc >= best) return;
				if (k == m) { best = acc; Array.Copy(choice, bestArr, m); return; }
				foreach (var (col, d) in e[k])
				{
					if (used[col]) continue;
					used[col] = true; choice[k] = col;
					Dfs1(k + 1, acc + d);
					used[col] = false;
				}
				choice[k] = -1;
				Dfs1(k + 1, acc + R);
			}
			Dfs1(0, 0.0);

			var alt = Enumerable.Repeat(double.PositiveInfinity, m).ToArray();
			var top = new List<(double Cost, int[] Choice)>();
			bool anyBound = bestArr.Any(x => x >= 0);

			void Dfs2(int k, double acc)
			{
				// отсечение: лист полезен, если дешевле какой-то текущей альтернативы или входит в k лучших
				double bound = 0.0;
				if (anyBound) for (int i = 0; i < m; i++) if (bestArr[i] >= 0 && alt[i] > bound) bound = alt[i];
				if (kBest > 0) bound = Math.Max(bound, top.Count < kBest ? double.PositiveInfinity : top[top.Count - 1].Cost);
				if (acc >= bound) return;
				if (k == m)
				{
					for (int i = 0; i < m; i++)
						if (bestArr[i] >= 0 && choice[i] != bestArr[i] && acc < alt[i]) alt[i] = acc;
					if (kBest > 0 && (top.Count < kBest || acc < top[top.Count - 1].Cost))
					{
						int pos = top.FindIndex(t => acc < t.Cost);
						if (pos < 0) pos = top.Count;
						top.Insert(pos, (acc, (int[])choice.Clone()));
						if (top.Count > kBest) top.RemoveAt(top.Count - 1);
					}
					return;
				}
				foreach (var (col, d) in e[k])
				{
					if (used[col]) continue;
					used[col] = true; choice[k] = col;
					Dfs2(k + 1, acc + d);
					used[col] = false;
				}
				choice[k] = -1;
				Dfs2(k + 1, acc + R);
			}
			Dfs2(0, 0.0);

			optCost = best;
			bestChoice = bestArr;
			altCost = alt;
			variants = top.Select(t => new Variant { Cost = t.Cost, Candidates = t.Choice }).ToList();
		}

		// ---------------------------------------------------------------- венгерский метод (крупные компоненты)

		/// <summary>
		/// Матрица (m + nCols) × (nCols + m): строки — точки и фиктивные строки «кандидат не занят» (цена 0),
		/// столбцы — кандидаты и по одному столбцу отказа на точку (цена R, для чужих точек Big).
		/// Δ точки — повторное решение с запрещённым ребром; k лучших — алгоритм Мурти на том же решателе.
		/// </summary>
		private static void LapComponent(List<List<(int Col, double Dist)>> e, int nCols, double R, int kBest,
			out double optCost, out int[] bestChoice, out double[] altCost, out List<Variant> variants)
		{
			int m = e.Count;
			int N = m + nCols;
			var baseCost = new double[N, N];
			for (int i = 0; i < N; i++)
				for (int j = 0; j < N; j++)
					baseCost[i, j] = i < m ? Big : 0.0;
			for (int k = 0; k < m; k++)
			{
				foreach (var (col, d) in e[k]) baseCost[k, col] = d;
				baseCost[k, nCols + k] = R;
			}
			var assign = new int[N];
			optCost = SolveMasked(baseCost, N, null, null, assign);
			bestChoice = new int[m];
			for (int k = 0; k < m; k++) bestChoice[k] = assign[k] < nCols ? assign[k] : -1;
			altCost = new double[m];
			var tmp = new int[N];
			for (int k = 0; k < m; k++)
				altCost[k] = bestChoice[k] >= 0
					? SolveMasked(baseCost, N, new[] { (k, assign[k]) }, null, tmp)
					: double.PositiveInfinity;
			variants = Murty(baseCost, N, m, nCols, kBest);
		}

		private static double SolveMasked(double[,] baseCost, int N, IList<(int Row, int Col)>? forbidden, IList<(int Row, int Col)>? fixedPairs, int[] assign)
		{
			var cost = (double[,])baseCost.Clone();
			if (forbidden != null)
				foreach (var (r, c) in forbidden) cost[r, c] = Big;
			if (fixedPairs != null)
				foreach (var (r, c) in fixedPairs)
				{
					for (int j = 0; j < N; j++) if (j != c) cost[r, j] = Big;
					for (int i = 0; i < N; i++) if (i != r) cost[i, c] = Big;
				}
			Hungarian.Solve(cost, assign);
			double total = 0.0;
			for (int i = 0; i < N; i++)
			{
				double v = cost[i, assign[i]];
				if (v >= Big) return double.PositiveInfinity;
				total += v;
			}
			return total;
		}

		/// <summary>k лучших назначений по возрастанию цены (Murty, 1968): разбиение пространства решений
		/// по строкам-точкам: строки до r фиксируются как в решении узла, ребро строки r запрещается.</summary>
		private static List<Variant> Murty(double[,] baseCost, int N, int m, int nCols, int kBest)
		{
			var result = new List<Variant>();
			if (kBest <= 0) return result;
			var queue = new List<(double Cost, int[] Assign, List<(int, int)> Fixed, List<(int, int)> Forbidden, long Seq)>();
			long seq = 0;
			var a0 = new int[N];
			double c0 = SolveMasked(baseCost, N, null, null, a0);
			if (double.IsPositiveInfinity(c0)) return result;
			queue.Add((c0, a0, new List<(int, int)>(), new List<(int, int)>(), seq++));
			while (result.Count < kBest && queue.Count > 0)
			{
				int bi = 0;
				for (int i = 1; i < queue.Count; i++)
					if (queue[i].Cost < queue[bi].Cost - 1e-12 || (Math.Abs(queue[i].Cost - queue[bi].Cost) <= 1e-12 && queue[i].Seq < queue[bi].Seq)) bi = i;
				var node = queue[bi];
				queue.RemoveAt(bi);
				result.Add(new Variant
				{
					Cost = node.Cost,
					Candidates = Enumerable.Range(0, m).Select(k => node.Assign[k] < nCols ? node.Assign[k] : -1).ToArray(),
				});
				var fixedRows = new HashSet<int>(node.Fixed.Select(f => f.Item1));
				var fixedAcc = new List<(int, int)>(node.Fixed);
				for (int r = 0; r < m; r++)
				{
					if (fixedRows.Contains(r)) continue;
					var forb = new List<(int, int)>(node.Forbidden) { (r, node.Assign[r]) };
					var a = new int[N];
					double c = SolveMasked(baseCost, N, forb, fixedAcc, a);
					if (!double.IsPositiveInfinity(c))
						queue.Add((c, a, new List<(int, int)>(fixedAcc), forb, seq++));
					fixedAcc.Add((r, node.Assign[r]));
				}
			}
			return result;
		}
	}
}
