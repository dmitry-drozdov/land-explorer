
using StreamJsonRpc;
using System;
using System.Threading.Tasks;

namespace MarkupServer
{
	public partial class LandService
	{
		[JsonRpcMethod("land/addAnchorAt", UseSingleObjectParameterDeserialization = true)]
		public Task<AddAnchorResultV2> AddAnchorAtAsync(AddAnchorParams p)
		{
			if (p == null || string.IsNullOrWhiteSpace(p.filePath))
				throw new ArgumentException("filePath is required");

			if (graphqlParser == null || typescriptParser == null)
				throw new InvalidOperationException("Parsers are not initialized. Call land/initialize first.");

			if (string.IsNullOrWhiteSpace(currentFolderPath))
				throw new InvalidOperationException("currentFolderPath is empty. Call land/listAnchors first.");

			TreeNode candidate;
			string message;
			try
			{
				candidate = TryCreateManualAnchorAtOffset(p.filePath, p.offset, out message);
			}
			catch (Exception ex)
			{
				Debug($"[addAnchor] cannot parse file '{p.filePath}': {ex.Message}");
				return Task.FromResult(MakeAddAnchorResult(null, null, $"Не удалось добавить точку: {ex.Message}"));
			}

			if (candidate == null)
				return Task.FromResult(MakeAddAnchorResult(null, null, message));

			lock (markupLock)
			{
				currentMarkup ??= new PersistedMarkup();
				currentMarkup.Anchors ??= new System.Collections.Generic.List<TreeNode>();

				var existing = FindEquivalentAnchorInMarkupUnsafe(candidate);
				if (existing != null)
				{
					var kindLabel = existing.IsManual ? "ручная" : "автоматическая";
					var existingMsg = $"Точка уже есть в разметке ({kindLabel}).";
					Debug($"[addAnchor] duplicate candidate -> {existing.Id} {existing.Filepath}:{existing.StartOffset}-{existing.EndOffset}");
					return Task.FromResult(MakeAddAnchorResult(CloneAnchor(existing), true, existingMsg));
				}

				candidate.IsManual = true;
				candidate.AnchorFamily = EnsureAnchorFamily(candidate);

				currentMarkup.Anchors.Add(CloneAnchor(candidate));
				RebuildRelationsUnsafe();
				RebuildMarkupRootsUnsafe();
				ReloadNodesByIdUnsafe();

				if (!AnchorStore.TrySave(currentFolderPath, currentMarkup, out var err))
					Debug($"[addAnchor] cannot save anchors cache: {err}");

				Debug($"[addAnchor] added manual anchor {candidate.Id} -> {candidate.Filepath}:{candidate.StartOffset}-{candidate.EndOffset} {candidate.Name}");
				return Task.FromResult(MakeAddAnchorResult(candidate, false, "Точка добавлена в разметку."));
			}
		}
	}
}
