using System;
using System.Collections.Generic;
using System.Linq;

namespace VPTree
{
	/// <summary>
	/// Проценты перепривязки: вероятности кандидатов одной точки и вариантов компоненты по ценам назначения.
	///
	/// Одна точка — softmax с температурой по кандидатам и варианту «не переносить» ценой R:
	///   P(c_i) = e^(−d_i/T) / (e^(−R/T) + Σ_j e^(−d_j/T)).
	/// Компонента — распределение Гиббса по всем допустимым расстановкам (кандидат достаётся не больше чем одной точке):
	///   P(σ) = e^(−C(σ)/T) / Σ_σ′ e^(−C(σ′)/T),   C(σ) = Σ d + R · (число точек без привязки);
	/// для точки без конкурентов совпадает с формулой одной точки. Процент строки «точка i → кандидат c» — сумма P(σ)
	/// по расстановкам, где σ_i = c: расстановки несовместны (верна ровно одна), их вероятности складываются
	/// (Колмогоров, 1933, гл. I, § 1); так же считают вероятность пары «отметка — цель» в JPDA (Fortmann и др., 1983).
	///
	/// Выбор пользователя в строке — условие: считаются только расстановки, совместимые с выбором. Проценты в списке
	/// строки, выбранной пользователем, считаются без её собственного выбора (иначе выбранный пункт всегда 100 %).
	///
	/// T подобрана максимумом правдоподобия на e11 tune (temperature scaling, Guo et al., 2017, § 4.2):
	/// markup/experiments/_explore/2026-09-27_distance_questions/results/calibration_params.json;
	/// ошибка калибровки ECE на e11 test и e12 — 0,029. Реализация повторяет scripts/common.py (gibbs_components).
	/// </summary>
	public static class AssignmentProbabilities
	{
		/// <summary>Температура, подобранная на e11 tune.</summary>
		public const double DefaultTemperature = 0.059553;

		/// <summary>Компоненты не больше этого числа точек считаются перебором всех расстановок (точно).</summary>
		public const int DefaultEnumerateLimit = 10;

		/// <summary>Для более крупных компонент проценты считаются по стольким лучшим расстановкам (Murty) — приблизительно.</summary>
		public const int DefaultApproxVariants = 100;

		/// <summary>Выбор «не переносить».</summary>
		public const int None = -1;

		/// <summary>Расстановки, у которых e^(−(C − C_min)/T) меньше e^(−Cutoff), в сумму не входят: их вклад ниже 10⁻²¹.</summary>
		private const double Cutoff = 50.0;

		/// <summary>Проценты одной точки: массив длины n + 1, последний элемент — «не переносить».</summary>
		public static double[] Point(IReadOnlyList<double> dists, double temperature, double rejectCost)
		{
			CheckTemperature(temperature);
			int n = dists?.Count ?? 0;
			var z = new double[n + 1];
			for (int i = 0; i < n; i++) z[i] = -dists![i] / temperature;
			z[n] = -rejectCost / temperature;
			double max = z.Max();
			double sum = 0.0;
			for (int i = 0; i <= n; i++) { z[i] = Math.Exp(z[i] - max); sum += z[i]; }
			for (int i = 0; i <= n; i++) z[i] /= sum;
			return z;
		}

		/// <summary>Одна расстановка: Choice[i] — кандидат точки i или <see cref="None"/>.</summary>
		public sealed class WeightedVariant
		{
			public double Cost;
			public double P;
			public int[] Choice = Array.Empty<int>();
		}

		public sealed class Distribution
		{
			/// <summary>true — учтены все расстановки; false — только лучшие (крупная компонента).</summary>
			public bool Exact = true;
			/// <summary>Сколько расстановок учтено (без пренебрежимо маловероятных).</summary>
			public long VariantCount;
			/// <summary>Лучшие расстановки по возрастанию цены.</summary>
			public List<WeightedVariant> Top = new List<WeightedVariant>();
			/// <summary>Процент строки: Rows[i][кандидат или None] — вероятность, что точка i переносится туда.</summary>
			public Dictionary<int, double>[] Rows = Array.Empty<Dictionary<int, double>>();

			public double RowP(int point, int choice)
				=> point >= 0 && point < Rows.Length && Rows[point].TryGetValue(choice, out var p) ? p : 0.0;
		}

		/// <summary>
		/// Распределение по расстановкам компоненты. <paramref name="edges"/>[i] — кандидаты точки i (индекс кандидата,
		/// расстояние); <paramref name="fixedChoice"/> — выбор пользователя по точкам (кандидат или <see cref="None"/>).
		/// Выбор, несовместимый с остальными (два одинаковых кандидата), даёт пустое распределение.
		/// </summary>
		public static Distribution Component(
			IReadOnlyList<IReadOnlyList<(int Cand, double Dist)>> edges,
			double temperature,
			double rejectCost,
			IReadOnlyDictionary<int, int>? fixedChoice = null,
			int topCount = 3,
			int enumerateLimit = DefaultEnumerateLimit,
			int approxVariants = DefaultApproxVariants)
		{
			CheckTemperature(temperature);
			int m = edges?.Count ?? 0;
			var result = new Distribution { Rows = Enumerable.Range(0, m).Select(_ => new Dictionary<int, double>()).ToArray() };
			if (m == 0) return result;

			// кандидаты точки: без дублей (минимальное расстояние), по возрастанию (d, индекс) — порядок обхода детерминирован
			var cands = new List<(int Cand, double Dist)>[m];
			for (int i = 0; i < m; i++)
			{
				var best = new Dictionary<int, double>();
				foreach (var (c, d) in edges![i] ?? Array.Empty<(int, double)>())
				{
					if (c < 0 || double.IsNaN(d)) continue;
					if (!best.TryGetValue(c, out var old) || d < old) best[c] = d;
				}
				var list = best.Select(kv => (kv.Key, kv.Value)).OrderBy(e => e.Value).ThenBy(e => e.Key).ToList();
				if (fixedChoice != null && fixedChoice.TryGetValue(i, out var fx))
				{
					if (fx == None) list.Clear();
					else list = list.Where(e => e.Key == fx).ToList();
					if (fx != None && list.Count == 0) return result;   // выбран кандидат, которого у точки нет
				}
				cands[i] = list;
			}
			var forcedBound = new bool[m];   // точка обязана быть привязанной (выбор пользователя — кандидат)
			if (fixedChoice != null)
				foreach (var kv in fixedChoice)
					if (kv.Key >= 0 && kv.Key < m && kv.Value != None) forcedBound[kv.Key] = true;

			return m <= enumerateLimit
				? Enumerate(cands, forcedBound, temperature, rejectCost, topCount, result)
				: Approximate(cands, forcedBound, temperature, rejectCost, topCount, approxVariants, result);
		}

		// ---------------------------------------------------------------- перебор всех расстановок

		private static Distribution Enumerate(List<(int Cand, double Dist)>[] cands, bool[] forcedBound, double t, double r,
			int topCount, Distribution result)
		{
			int m = cands.Length;
			var used = new HashSet<int>();
			var choice = new int[m];

			// 1) минимальная цена (ветви и границы)
			double min = double.PositiveInfinity;
			void Dfs1(int k, double acc)
			{
				if (acc >= min) return;
				if (k == m) { min = acc; return; }
				foreach (var (c, d) in cands[k])
				{
					if (!used.Add(c)) continue;
					Dfs1(k + 1, acc + d);
					used.Remove(c);
				}
				if (!forcedBound[k]) Dfs1(k + 1, acc + r);
			}
			Dfs1(0, 0.0);
			if (double.IsPositiveInfinity(min)) return result;   // выбор пользователя несовместим

			// 2) все расстановки: сумма весов, проценты строк, лучшие варианты
			double z = 0.0;
			var sums = Enumerable.Range(0, m).Select(_ => new Dictionary<int, double>()).ToArray();
			var top = new List<(double Cost, double W, int[] Choice)>();
			long count = 0;
			void Dfs2(int k, double acc)
			{
				if ((acc - min) / t > Cutoff) return;   // все листья ниже не дешевле acc: вклад пренебрежим
				if (k == m)
				{
					double w = Math.Exp(-(acc - min) / t);
					z += w;
					count++;
					for (int i = 0; i < m; i++)
					{
						sums[i].TryGetValue(choice[i], out var s);
						sums[i][choice[i]] = s + w;
					}
					InsertTop(top, topCount, acc, w, choice);
					return;
				}
				foreach (var (c, d) in cands[k])
				{
					if (!used.Add(c)) continue;
					choice[k] = c;
					Dfs2(k + 1, acc + d);
					used.Remove(c);
				}
				if (!forcedBound[k])
				{
					choice[k] = None;
					Dfs2(k + 1, acc + r);
				}
			}
			Dfs2(0, 0.0);

			result.Exact = true;
			result.VariantCount = count;
			Finish(result, sums, top, z);
			return result;
		}

		// ---------------------------------------------------------------- крупные компоненты: лучшие расстановки (Murty)

		private static Distribution Approximate(List<(int Cand, double Dist)>[] cands, bool[] forcedBound, double t, double r,
			int topCount, int approxVariants, Distribution result)
		{
			int m = cands.Length;
			var cols = cands.SelectMany(x => x.Select(e => e.Cand)).Distinct().OrderBy(c => c).ToList();
			var colOf = new Dictionary<int, int>();
			for (int j = 0; j < cols.Count; j++) colOf[cols[j]] = j;
			int nCols = cols.Count, N = m + nCols;

			// матрица как в AssignmentRebind.LapComponent: строки — точки и фиктивные строки «кандидат не занят»,
			// столбцы — кандидаты и по столбцу отказа на точку; выбор пользователя — через запрет остальных клеток
			var cost = new double[N, N];
			for (int i = 0; i < N; i++)
				for (int j = 0; j < N; j++)
					cost[i, j] = i < m ? AssignmentRebind.Big : 0.0;
			for (int k = 0; k < m; k++)
			{
				foreach (var (c, d) in cands[k]) cost[k, colOf[c]] = d;
				if (!forcedBound[k]) cost[k, nCols + k] = r;
			}

			var variants = AssignmentRebind.Murty(cost, N, m, nCols, Math.Max(1, approxVariants));
			if (variants.Count == 0) return result;
			double min = variants[0].Cost;
			double z = 0.0;
			var sums = Enumerable.Range(0, m).Select(_ => new Dictionary<int, double>()).ToArray();
			var top = new List<(double Cost, double W, int[] Choice)>();
			foreach (var v in variants)
			{
				double w = Math.Exp(-(v.Cost - min) / t);
				var choice = v.Candidates.Select(col => col < 0 ? None : cols[col]).ToArray();
				z += w;
				for (int i = 0; i < m; i++)
				{
					sums[i].TryGetValue(choice[i], out var s);
					sums[i][choice[i]] = s + w;
				}
				InsertTop(top, topCount, v.Cost, w, choice);
			}

			// меньше расстановок, чем запрошено, — пространство исчерпано, результат точный
			result.Exact = variants.Count < approxVariants;
			result.VariantCount = variants.Count;
			Finish(result, sums, top, z);
			return result;
		}

		// ---------------------------------------------------------------- общее

		private static void InsertTop(List<(double Cost, double W, int[] Choice)> top, int topCount, double cost, double w, int[] choice)
		{
			if (topCount <= 0) return;
			if (top.Count >= topCount && !Before(cost, choice, top[top.Count - 1].Cost, top[top.Count - 1].Choice)) return;
			int pos = top.FindIndex(x => Before(cost, choice, x.Cost, x.Choice));
			if (pos < 0) pos = top.Count;
			top.Insert(pos, (cost, w, (int[])choice.Clone()));
			if (top.Count > topCount) top.RemoveAt(top.Count - 1);
		}

		/// <summary>Порядок вариантов: по цене, при равной цене — лексикографически по выбору (детерминизм).</summary>
		private static bool Before(double costA, int[] a, double costB, int[] b)
		{
			if (costA < costB - 1e-12) return true;
			if (costA > costB + 1e-12) return false;
			for (int i = 0; i < a.Length; i++)
				if (a[i] != b[i]) return a[i] < b[i];
			return false;
		}

		private static void Finish(Distribution result, Dictionary<int, double>[] sums, List<(double Cost, double W, int[] Choice)> top, double z)
		{
			for (int i = 0; i < sums.Length; i++)
				result.Rows[i] = sums[i].ToDictionary(kv => kv.Key, kv => kv.Value / z);
			result.Top = top.Select(x => new WeightedVariant { Cost = x.Cost, P = x.W / z, Choice = x.Choice }).ToList();
		}

		private static void CheckTemperature(double t)
		{
			if (!(t > 0.0) || double.IsInfinity(t)) throw new ArgumentException("temperature must be positive and finite");
		}
	}
}
