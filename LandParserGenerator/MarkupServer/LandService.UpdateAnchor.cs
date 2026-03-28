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
		private void BuildCurrentRebindingForestsFromDisk()
		{
			if (string.IsNullOrWhiteSpace(currentFolderPath))
				throw new InvalidOperationException("currentFolderPath is empty. Call land/listAnchors first.");

			BuildSemanticMarkupFromDisk(out var gqlAnchors, out var tsAnchors);
			LoadRebindingForestsFromAnchors(gqlAnchors.Concat(tsAnchors));
		}

		[JsonRpcMethod("land/updateAnchor", UseSingleObjectParameterDeserialization = true)]
		public Task<UpdateAnchorResultV2> UpdateAnchorAsync(UpdateAnchorParams p)
		{
			if (p == null || string.IsNullOrWhiteSpace(p.anchorId))
				throw new ArgumentException("anchorId is required");

			if (!nodesById.TryGetValue(p.anchorId, out var oldNode) || oldNode == null)
			{
				Debug($"not found anchor by id [{p.anchorId}] (cache lost). Please run land/listAnchors again.");
				return Task.FromResult(MakeUpdateAnchorResult(null));
			}

			EnsureAnchorFamily(oldNode);
			var profileKey = GetProfileKey(oldNode);
			if (string.IsNullOrWhiteSpace(profileKey))
			{
				Debug($"[updateAnchor] anchor [{p.anchorId}] has unsupported rebind profile.");
				return Task.FromResult(MakeUpdateAnchorResult(null));
			}

			try
			{
				BuildCurrentRebindingForestsFromDisk();
			}
			catch (Exception ex)
			{
				Debug($"[updateAnchor] cannot rebuild rebinding forest: {ex.Message}");
				return Task.FromResult(MakeUpdateAnchorResult(null));
			}

			if (!currentTreesByProfileKey.TryGetValue(profileKey, out var tree)
				|| !currentContextsByProfileKey.TryGetValue(profileKey, out var contexts)
				|| !currentNodesByProfileKey.TryGetValue(profileKey, out var nodes)
				|| contexts.Count == 0)
			{
				Debug($"[updateAnchor] no index for profile='{profileKey}'.");
				return Task.FromResult(MakeUpdateAnchorResult(null));
			}

			var query = BuildAnchorContext(oldNode);
			var cands = tree.KNearest(query, 1);
			if (cands == null || cands.Count == 0)
				return Task.FromResult(MakeUpdateAnchorResult(null));

			var best = cands[0];
			var newNode = nodes[best.Index];
			Debug($"[updateAnchor] profile={profileKey} dist={best.Dist} -> {newNode?.Filepath}:{newNode?.StartOffset}-{newNode?.EndOffset} {newNode?.Name}");

			var updated = CloneAnchor(newNode);
			updated.Id = p.anchorId;
			updated.IsManual = oldNode.IsManual;
			updated.AnchorFamily = EnsureAnchorFamily(updated);

			var isGql = string.Equals(updated.Language, LangGql, StringComparison.OrdinalIgnoreCase);
			var parentGroupId = isGql ? GetTypeGroupId(updated) : null;
			var parentGroupName = isGql ? GetTypeGroupName(updated) : null;
			var fieldGroupId = isGql ? GetFieldGroupId(updated) : null;
			var fieldGroupName = isGql ? GetFieldGroupName(updated) : null;

			nodesById[p.anchorId] = updated;

			try
			{
				lock (markupLock)
				{
					ReplaceAnchorInMarkupUnsafe(updated);
					RebuildRelationsUnsafe();
					RebuildMarkupRootsUnsafe();
					ReloadNodesByIdUnsafe();
					if (!AnchorStore.TrySave(currentFolderPath, currentMarkup, out var err))
						Debug($"[updateAnchor] cannot save anchors cache: {err}");
				}
			}
			catch (Exception ex)
			{
				Debug($"[updateAnchor] cache persist failed: {ex.Message}");
			}

			return Task.FromResult(MakeUpdateAnchorResult(updated, parentGroupId, parentGroupName, fieldGroupId, fieldGroupName));
		}
	}
}
