using StreamJsonRpc;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using VPTree;

namespace MarkupServer
{
	public partial class LandService
	{
		internal void BuildCurrentRebindingForestsFromDisk()
		{
			if (string.IsNullOrWhiteSpace(currentFolderPath))
				throw new InvalidOperationException("currentFolderPath is empty. Call land/listAnchors first.");

			BuildSemanticMarkupFromDisk(out var gqlAnchors, out var tsAnchors);
			LoadRebindingForestsFromAnchors(gqlAnchors.Concat(tsAnchors));
		}

		/// <summary>
		/// Перепривязка точки по запросу пользователя — тем же конвейером, что полный рескан: точный предпроход забирает
		/// неизменённые поля (занятое другой точкой поле не станет кандидатом), шаг назначения решает компоненты с учётом
		/// соперников. Уверенные решения применяются, как при рескане; спорная компонента выбранной точки сохраняется,
		/// даже если данных пользователя в ней нет, — плагин открывает окно выбора. Для точки без соперников назначение
		/// совпадает с поточечным правилом (R = τ / (1 − m)), так что простой случай не меняется.
		/// </summary>
		[JsonRpcMethod("land/updateAnchor", UseSingleObjectParameterDeserialization = true)]
		public Task<UpdateAnchorResultV2> UpdateAnchorAsync(UpdateAnchorParams p)
		{
			if (p == null || string.IsNullOrWhiteSpace(p.anchorId))
				throw new ArgumentException("anchorId is required");

			if (!nodesById.TryGetValue(p.anchorId, out var oldNode) || oldNode == null || string.IsNullOrWhiteSpace(currentFolderPath))
			{
				Debug($"not found anchor by id [{p.anchorId}] (cache lost). Please run land/listAnchors again.");
				return Task.FromResult(MakeUpdateAnchorResult(null));
			}

			var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			try
			{
				RescanProject(new[] { p.anchorId }, merged);
			}
			catch (Exception ex)
			{
				Debug($"[updateAnchor] rescan failed: {ex.Message}");
				return Task.FromResult(MakeUpdateAnchorResult(null));
			}

			lock (markupLock)
			{
				var dispute = (currentMarkup?.Disputes ?? new List<DisputedComponent>()).FirstOrDefault(d => d?.Points != null
					&& d.Points.Any(pt => string.Equals(pt.AnchorId, p.anchorId, StringComparison.OrdinalIgnoreCase) && pt.Status != DisputeAccepted));
				if (dispute != null)
				{
					Debug($"[updateAnchor] {p.anchorId}: disputed component {dispute.Id} ({dispute.Points.Count} points)");
					return Task.FromResult(new UpdateAnchorResultV2
					{
						st = "dispute",
						did = dispute.Id,
						rs = dispute.Points.Count > 1
							? "точка претендует на те же места, что и другие точки — нужен ваш выбор"
							: "несколько мест в новом коде похожи почти одинаково — нужен ваш выбор",
					});
				}

				var id = merged.TryGetValue(p.anchorId, out var into) ? into : p.anchorId;
				if (LiveAnchorsByIdUnsafe().TryGetValue(id, out var node))
				{
					var updated = CloneAnchor(node);
					var isGql = string.Equals(updated.Language, LangGql, StringComparison.OrdinalIgnoreCase);
					Debug($"[updateAnchor] {p.anchorId} -> {updated.Filepath}:{updated.StartOffset}-{updated.EndOffset} {updated.Name}");
					return Task.FromResult(MakeUpdateAnchorResult(updated,
						isGql ? GetTypeGroupId(updated) : null,
						isGql ? GetTypeGroupName(updated) : null,
						isGql ? GetFieldGroupId(updated) : null,
						isGql ? GetFieldGroupName(updated) : null));
				}

				var lost = (currentMarkup?.LostAnchors ?? new List<LostAnchor>())
					.Any(l => string.Equals(l?.AnchorId, p.anchorId, StringComparison.OrdinalIgnoreCase));
				Debug($"[updateAnchor] {p.anchorId}: {(lost ? "lost (in _Lost)" : "gone")}");
				return Task.FromResult(new UpdateAnchorResultV2
				{
					st = lost ? "lost" : "gone",
					rs = lost ? "в новом коде нет близкого места — точка в _Lost" : "точки нет в новом коде",
				});
			}
		}
	}
}
