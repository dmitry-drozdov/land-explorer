using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using VPTree;

namespace Land.Markup
{
	public sealed class ExactHashRebinder
	{
		private readonly Dictionary<ulong, List<MethodAnchor>> _map;
		private readonly HashSet<string> _takenOld = new HashSet<string>(StringComparer.Ordinal);

		public ExactHashRebinder(IEnumerable<MethodAnchor> oldAnchors)
		{
			_map = new Dictionary<ulong, List<MethodAnchor>>();
			foreach (var o in oldAnchors)
			{
				var key = AnchorKeys.Fnv1a64(AnchorKeys.CanonExact(o));
				List<MethodAnchor> lst;
				if (!_map.TryGetValue(key, out lst))
				{
					lst = new List<MethodAnchor>(1);
					_map[key] = lst;
				}
				lst.Add(o);
			}
		}

		// Возвращает только результаты по точному совпадению.
		// Остальных (NoMatch/Ambiguous) — потом в VP-проход.
		public List<MatchResult> RebindExact(IEnumerable<MethodAnchor> newAnchors)
		{
			var results = new List<MatchResult>();

			foreach (var n in newAnchors)
			{
				var key = AnchorKeys.Fnv1a64(AnchorKeys.CanonExact(n));
				List<MethodAnchor> lst;
				if (!_map.TryGetValue(key, out lst) || lst.Count == 0)
				{
					results.Add(new MatchResult { New = n, Status = MatchStatus.NoMatch });
					continue;
				}

				// Учитываем one-to-one: исключаем уже занятых старых
				var candidates = new List<MethodAnchor>();
				for (int i = 0; i < lst.Count; i++)
				{
					var o = lst[i];
					if (!_takenOld.Contains(o.Id)) candidates.Add(o);
				}

				if (candidates.Count == 1)
				{
					var o = candidates[0];
					_takenOld.Add(o.Id);
					results.Add(new MatchResult
					{
						New = n,
						Old = o,
						BestDist = 0.0,
						SecondDist = null,
						Status = MatchStatus.Accepted
					});
				}
				else if (candidates.Count > 1)
				{
					// Под одним exact-ключом несколько свободных старых — это дубли → на ручную
					var mr = new MatchResult { New = n, Status = MatchStatus.Ambiguous };
					for (int i = 0; i < candidates.Count; i++)
						mr.Candidates.Add(new CandidateOld { Old = candidates[i], Dist = 0.0 });
					results.Add(mr);
				}
				else
				{
					// Все exact-кандидаты заняты → отдаём дальше в kNN, вдруг есть близкая альтернатива
					results.Add(new MatchResult { New = n, Status = MatchStatus.NoMatch });
				}
			}

			return results;
		}

		public IReadOnlyCollection<string> TakenOldIds
		{
			get { return _takenOld; }
		}
	}
}
