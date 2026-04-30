using StreamJsonRpc;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace MarkupServer
{
	public partial class LandService
	{
		// =========================================================================
		// Пользовательский комментарий — поле Comment у якоря или user-группы.
		//
		// Универсальный RPC: target определяется по префиксу Id.
		// Пустой/null комментарий = удаление.
		//
		// Comment живёт по стабильному AnchorId / UserGroup.Id, поэтому переживает
		// rebind через VP-tree, переименования группы, forceRescan (см. перенос
		// в RebindAnchorAgainstCurrentForests, UpdateAnchorAsync, StitchAutoAnchorIdsFromPrevious).
		// =========================================================================

		[JsonRpcMethod("land/setComment", UseSingleObjectParameterDeserialization = true)]
		public Task<SetCommentResultV2> SetCommentAsync(SetCommentParams p)
		{
			if (p == null || string.IsNullOrWhiteSpace(p.targetId))
				throw new ArgumentException("targetId is required");
			if (string.IsNullOrWhiteSpace(currentFolderPath))
				throw new InvalidOperationException("currentFolderPath is empty. Call land/listAnchors first.");

			var trimmed = string.IsNullOrWhiteSpace(p.comment) ? null : p.comment.Trim();

			lock (markupLock)
			{
				EnsureMarkupInitializedUnsafe();

				// User-группа: префикс userGroup:
				if (p.targetId.StartsWith(UserGroupIdPrefix, StringComparison.Ordinal))
				{
					var grp = currentMarkup.UserGroups
						.FirstOrDefault(g => string.Equals(g?.Id, p.targetId, StringComparison.OrdinalIgnoreCase));
					if (grp == null)
						return Task.FromResult(new SetCommentResultV2 { ok = false, m = "Папка не найдена." });

					grp.Comment = trimmed;
					grp.UpdatedAtUtc = DateTime.UtcNow;

					PersistMarkupUnsafe("setComment");
					Debug($"[setComment] userGroup {p.targetId} -> {(trimmed == null ? "(cleared)" : $"len={trimmed.Length}")}");
					return Task.FromResult(new SetCommentResultV2 { ok = true, m = trimmed == null ? "Комментарий удалён." : "Комментарий сохранён." });
				}

				// Якорь — поиск по Id в Anchors. Lost-якоря (snapshot в LostAnchors) тоже поддерживаем —
				// иногда полезно поправить комментарий на потерянном перед recovery.
				var anchor = currentMarkup.Anchors
					.FirstOrDefault(a => a != null
						&& string.Equals(a.NodeType, "anchor", StringComparison.OrdinalIgnoreCase)
						&& string.Equals(a.Id, p.targetId, StringComparison.OrdinalIgnoreCase));
				if (anchor != null)
				{
					anchor.Comment = trimmed;
					PersistMarkupUnsafe("setComment");
					Debug($"[setComment] anchor {p.targetId} -> {(trimmed == null ? "(cleared)" : $"len={trimmed.Length}")}");
					return Task.FromResult(new SetCommentResultV2 { ok = true, m = trimmed == null ? "Комментарий удалён." : "Комментарий сохранён." });
				}

				var lost = (currentMarkup.LostAnchors ?? new System.Collections.Generic.List<LostAnchor>())
					.FirstOrDefault(l => string.Equals(l?.AnchorId, p.targetId, StringComparison.OrdinalIgnoreCase));
				if (lost != null)
				{
					lost.Comment = trimmed;
					PersistMarkupUnsafe("setComment");
					Debug($"[setComment] lostAnchor {p.targetId} -> {(trimmed == null ? "(cleared)" : $"len={trimmed.Length}")}");
					return Task.FromResult(new SetCommentResultV2 { ok = true, m = trimmed == null ? "Комментарий удалён." : "Комментарий сохранён." });
				}

				return Task.FromResult(new SetCommentResultV2 { ok = false, m = "Цель не найдена в текущем снимке." });
			}
		}
	}
}
