using System;
using System.Collections.Generic;

namespace VPTree
{
	/// <summary>
	/// Слой 1 перепривязки сервера плагина (сшивка Id при пересканировании): точное совпадение ключа
	/// «язык | вид | путь файла | исходное имя родителя | нормализованное имя | сигнатура». Сигнатура (параметры и результаты,
	/// <see cref="AnchorKeys.Signature"/>) есть у вызываемых элементов — полей GraphQL, функций Go, членов TS; у объявлений типов
	/// её нет. Добавлена 2026-10-15 (e21): без неё поля, обменявшиеся именами, перепривязывались по имени к чужому полю.
	/// Старые точки обходятся в заданном порядке; если в новой версии несколько свободных точек с тем же ключом,
	/// берётся ближайшая по смещению начала (при равенстве — первая в порядке новых точек).
	/// Общий код для сервера (LandService.StitchAutoAnchorIdsFromPrevious) и стенда (VPTreeBench assign-batch
	/// --prepass server), чтобы эксперименты измеряли ровно то, что работает в плагине (серия e21_server_stitch_mode).
	/// </summary>
	public static class StitchLayer1
	{
		public static string Key(string language, string kind, string filePathNorm, string parentRaw, string methodNameNorm, string signature = null)
			=> signature == null
				? $"{language}|{kind}|{filePathNorm}|{parentRaw}|{methodNameNorm}"
				: $"{language}|{kind}|{filePathNorm}|{parentRaw}|{methodNameNorm}|{signature}";

		/// <summary>
		/// Ключ слоя 1б: тот же ключ без пути файла — «язык | вид | исходное имя родителя | нормализованное имя | сигнатура».
		/// Только для вызываемых элементов (сигнатура обязательна); у остальных слоя 1б нет (null).
		/// </summary>
		public static string GlobalKey(string language, string kind, string parentRaw, string methodNameNorm, string signature)
			=> signature == null ? null : $"{language}|{kind}|{parentRaw}|{methodNameNorm}|{signature}";

		/// <summary>
		/// Сопоставление по ключу. prevKeys / freshKeys — ключи (null — точка в слое 1 не участвует), offsets — смещения начала.
		/// Слой 1б (если переданы prevGlobalKeys / freshGlobalKeys): для точек, оставшихся после слоя 1, — совпадение ключа без
		/// пути файла, только единственное и среди оставшихся старых, и среди свободных новых (перенос без изменений в другой
		/// файл; добавлен 2026-10-15, e21: «сначала в своём файле, потом во всех»).
		/// Возвращает для каждой старой точки индекс новой или −1.
		/// </summary>
		public static int[] Match(IReadOnlyList<string> prevKeys, IReadOnlyList<int> prevOffsets,
			IReadOnlyList<string> freshKeys, IReadOnlyList<int> freshOffsets,
			IReadOnlyList<string> prevGlobalKeys = null, IReadOnlyList<string> freshGlobalKeys = null)
		{
			var result = MatchInFile(prevKeys, prevOffsets, freshKeys, freshOffsets, out var claimed);
			if (prevGlobalKeys == null || freshGlobalKeys == null)
				return result;

			var prevByKey = new Dictionary<string, List<int>>(StringComparer.Ordinal);
			for (int p = 0; p < prevGlobalKeys.Count; p++)
			{
				if (result[p] >= 0 || prevGlobalKeys[p] == null) continue;
				if (!prevByKey.TryGetValue(prevGlobalKeys[p], out var l)) prevByKey[prevGlobalKeys[p]] = l = new List<int>();
				l.Add(p);
			}
			var freshByKey = new Dictionary<string, List<int>>(StringComparer.Ordinal);
			for (int f = 0; f < freshGlobalKeys.Count; f++)
			{
				if (claimed[f] || freshGlobalKeys[f] == null) continue;
				if (!freshByKey.TryGetValue(freshGlobalKeys[f], out var l)) freshByKey[freshGlobalKeys[f]] = l = new List<int>();
				l.Add(f);
			}
			foreach (var kv in prevByKey)
				if (kv.Value.Count == 1 && freshByKey.TryGetValue(kv.Key, out var fl) && fl.Count == 1)
				{
					result[kv.Value[0]] = fl[0];
					claimed[fl[0]] = true;
				}
			return result;
		}

		static int[] MatchInFile(IReadOnlyList<string> prevKeys, IReadOnlyList<int> prevOffsets,
			IReadOnlyList<string> freshKeys, IReadOnlyList<int> freshOffsets, out bool[] claimedOut)
		{
			var byKey = new Dictionary<string, List<int>>(StringComparer.Ordinal);
			for (int i = 0; i < freshKeys.Count; i++)
			{
				var k = freshKeys[i];
				if (k == null) continue;
				if (!byKey.TryGetValue(k, out var bucket))
					byKey[k] = bucket = new List<int>();
				bucket.Add(i);
			}

			var claimed = new bool[freshKeys.Count];
			var result = new int[prevKeys.Count];
			for (int p = 0; p < prevKeys.Count; p++)
			{
				result[p] = -1;
				var k = prevKeys[p];
				if (k == null || !byKey.TryGetValue(k, out var candidates))
					continue;
				int best = -1, bestDelta = int.MaxValue;
				foreach (var c in candidates)
				{
					if (claimed[c]) continue;
					var delta = Math.Abs(prevOffsets[p] - freshOffsets[c]);
					if (delta < bestDelta)
					{
						bestDelta = delta;
						best = c;
					}
				}
				if (best < 0) continue;
				claimed[best] = true;
				result[p] = best;
			}
			claimedOut = claimed;
			return result;
		}
	}
}
