using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using VPTree;

namespace Land.Markup
{
	public enum MatchStatus
	{
		Accepted,   // уверенно перепривязали
		Ambiguous,  // кандидаты есть, но уверенности нет → на ручную
		NoMatch     // ничего подходящего
	}

	public sealed class CandidateOld
	{
		public MethodAnchor Old;
		public double Dist; // полная метрика
	}

	public sealed class MatchResult
	{
		public MethodAnchor New;
		public MethodAnchor Old;           // если Accepted
		public double BestDist;
		public double? SecondDist;
		public MatchStatus Status;
		public List<CandidateOld> Candidates = new List<CandidateOld>(); // топ-k для UI
	}

	public sealed class RebindEngine
	{
		private readonly Rebinder _rebinder;                 // построен по СТАРОМУ набору
		private readonly List<MethodAnchor> _oldAnchors;     // тот же список, что внутри _rebinder
		private readonly Dist.Weights _w;

		public RebindEngine(Rebinder rebinder, List<MethodAnchor> oldAnchors, Dist.Weights w = null)
		{
			_rebinder = rebinder;
			_oldAnchors = oldAnchors;
			_w = w ?? new Dist.Weights();
		}

		/// <param name="newAnchors">Новые (после изменений) точки</param>
		/// <param name="k">сколько ближайших брать на кандидатов</param>
		/// <param name="tau">порог «достаточно близко»</param>
		/// <param name="margin">минимальная разница между 1-м и 2-м кандидатом</param>
		public List<MatchResult> Rebind(IEnumerable<MethodAnchor> newAnchors, int k = 3, double tau = 0.18, double margin = 0.02)
		{
			var results = new List<MatchResult>();
			var proposals = new List<MatchResult>();       // только те, кто «почти уверен»

			// 1) kNN для каждой новой точки
			foreach (var n in newAnchors)
			{
				var knn = _rebinder.Query(n, k); // вернёт (Anchor, Dist)[]

				var mr = new MatchResult { New = n };
				for (int i = 0; i < knn.Count; i++)
				{
					mr.Candidates.Add(new CandidateOld { Old = _oldAnchors[knn[i].Index], Dist = knn[i].Dist });
				}

				if (knn.Count == 0)
				{
					mr.Status = MatchStatus.NoMatch;
					results.Add(mr);
					continue;
				}

				mr.BestDist = knn[0].Dist;
				mr.SecondDist = (knn.Count > 1 ? (double?)knn[1].Dist : null);

				// Предварительное решение «один-к-одному»: отмечаем как «можно принять»
				bool confident = (mr.BestDist <= tau) && (mr.SecondDist == null || (mr.SecondDist.Value - mr.BestDist) >= margin);
				if (confident)
				{
					mr.Status = MatchStatus.Accepted; // пока как предложение (может поменяться из-за коллизий)
					proposals.Add(mr);
				}
				else
				{
					mr.Status = (mr.BestDist <= tau) ? MatchStatus.Ambiguous : MatchStatus.NoMatch;
					results.Add(mr);
				}
			}

			// 2) Разрешаем конкуренцию: один old не должен назначиться нескольким new
			// Сортируем предложения по возрастанию лучшей дистанции — «самые очевидные» разбираем первыми
			proposals.Sort((a, b) => a.BestDist.CompareTo(b.BestDist));

			var takenOld = new HashSet<string>(); // Old.Id, какие уже заняты
			var finalAccepted = new List<MatchResult>();

			foreach (var mr in proposals)
			{
				// если лучший old свободен — берем
				var best = mr.Candidates[0].Old;
				if (!takenOld.Contains(best.Id))
				{
					takenOld.Add(best.Id);
					finalAccepted.Add(mr); // статус уже Accepted
					continue;
				}

				// иначе пробуем следующий(ие) кандидаты в пределах порога tau
				bool placed = false;
				for (int i = 1; i < mr.Candidates.Count; i++)
				{
					var c = mr.Candidates[i];
					if (c.Dist <= tau && !takenOld.Contains(c.Old.Id))
					{
						// переопределяем best на альтернативу
						mr.BestDist = c.Dist;
						mr.Old = c.Old;
						takenOld.Add(c.Old.Id);
						finalAccepted.Add(mr);
						placed = true;
						break;
					}
				}

				if (!placed)
				{
					// не удалось однозначно пристроить — в «Ambiguous»
					mr.Status = MatchStatus.Ambiguous;
					results.Add(mr);
				}
			}

			// 3) Финализируем «принятые»: дополняем Old, статус уже стоит
			foreach (var mr in finalAccepted)
			{
				if (mr.Old == null) mr.Old = mr.Candidates[0].Old;
				results.Add(mr);
			}

			return results;
		}
	}

}
