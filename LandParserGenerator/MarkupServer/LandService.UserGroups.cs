using StreamJsonRpc;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace MarkupServer
{
	public partial class LandService
	{
		// =========================================================================
		// User-группы и multi-membership.
		//
		// Все RPC требуют, чтобы хотя бы раз был вызван land/listAnchors —
		// без этого currentMarkup пуст, и нечего трогать.
		//
		// Все мутирующие операции выполняются под markupLock, обновляют snapshot
		// на диске и пересобирают Roots+nodesById, чтобы клиент после reload получил
		// консистентное дерево.
		// =========================================================================

		private const string UserGroupIdPrefix = "userGroup:";

		private static UserGroupResultV2 MakeUserGroupResult(UserGroup group, string message = null)
		{
			TreeNodeClientV2 g = null;
			if (group != null)
			{
				g = new TreeNodeClientV2
				{
					id = group.Id,
					n = group.Name,
					t = "group",
					gk = "user",
				};
			}

			return new UserGroupResultV2 { g = g, m = message };
		}

		private static UserGroupSimpleResultV2 MakeSimpleResult(bool ok, string message = null, bool? alreadyMember = null, int? removedMemberships = null)
			=> new UserGroupSimpleResultV2
			{
				ok = ok,
				m = message,
				alreadyMember = alreadyMember,
				removedMemberships = removedMemberships,
			};

		private void EnsureMarkupInitializedUnsafe()
		{
			currentMarkup ??= new PersistedMarkup();
			currentMarkup.Anchors ??= new List<TreeNode>();
			currentMarkup.UserGroups ??= new List<UserGroup>();
			currentMarkup.Memberships ??= new List<UserGroupMembership>();
		}

		private void PersistMarkupUnsafe(string operationTag)
		{
			if (string.IsNullOrWhiteSpace(currentFolderPath))
				return;

			RebuildMarkupRootsUnsafe();
			ReloadNodesByIdUnsafe();
			if (!AnchorStore.TrySave(currentFolderPath, currentMarkup, out var err))
				Debug($"[{operationTag}] cannot save anchors cache: {err}");
		}

		[JsonRpcMethod("land/createUserGroup", UseSingleObjectParameterDeserialization = true)]
		public Task<UserGroupResultV2> CreateUserGroupAsync(CreateUserGroupParams p)
		{
			if (p == null)
				throw new ArgumentException("params is required");
			if (string.IsNullOrWhiteSpace(currentFolderPath))
				throw new InvalidOperationException("currentFolderPath is empty. Call land/listAnchors first.");

			var name = (p.name ?? string.Empty).Trim();
			if (string.IsNullOrWhiteSpace(name))
				return Task.FromResult(MakeUserGroupResult(null, "Имя группы не может быть пустым."));

			// ParentGroupId зарезервирован, но в MVP всегда null.
			if (!string.IsNullOrWhiteSpace(p.parentGroupId))
				Debug($"[createUserGroup] parentGroupId='{p.parentGroupId}' ignored (hierarchy not supported in MVP)");

			lock (markupLock)
			{
				EnsureMarkupInitializedUnsafe();

				var group = new UserGroup
				{
					Id = UserGroupIdPrefix + Guid.NewGuid().ToString("N"),
					Name = name,
					ParentGroupId = null,
					Order = null,
					CreatedAtUtc = DateTime.UtcNow,
					UpdatedAtUtc = DateTime.UtcNow,
				};
				currentMarkup.UserGroups.Add(group);

				PersistMarkupUnsafe("createUserGroup");
				Debug($"[createUserGroup] created '{group.Name}' (id={group.Id})");
				return Task.FromResult(MakeUserGroupResult(group, "Группа создана."));
			}
		}

		[JsonRpcMethod("land/renameUserGroup", UseSingleObjectParameterDeserialization = true)]
		public Task<UserGroupResultV2> RenameUserGroupAsync(RenameUserGroupParams p)
		{
			if (p == null || string.IsNullOrWhiteSpace(p.groupId))
				throw new ArgumentException("groupId is required");
			if (string.IsNullOrWhiteSpace(currentFolderPath))
				throw new InvalidOperationException("currentFolderPath is empty. Call land/listAnchors first.");

			var name = (p.name ?? string.Empty).Trim();
			if (string.IsNullOrWhiteSpace(name))
				return Task.FromResult(MakeUserGroupResult(null, "Имя группы не может быть пустым."));

			lock (markupLock)
			{
				EnsureMarkupInitializedUnsafe();

				var group = currentMarkup.UserGroups
					.FirstOrDefault(g => string.Equals(g?.Id, p.groupId, StringComparison.OrdinalIgnoreCase));
				if (group == null)
				{
					Debug($"[renameUserGroup] group not found: {p.groupId}");
					return Task.FromResult(MakeUserGroupResult(null, "Группа не найдена."));
				}

				group.Name = name;
				group.UpdatedAtUtc = DateTime.UtcNow;

				PersistMarkupUnsafe("renameUserGroup");
				Debug($"[renameUserGroup] renamed {group.Id} -> '{group.Name}'");
				return Task.FromResult(MakeUserGroupResult(group, "Группа переименована."));
			}
		}

		[JsonRpcMethod("land/deleteUserGroup", UseSingleObjectParameterDeserialization = true)]
		public Task<UserGroupSimpleResultV2> DeleteUserGroupAsync(DeleteUserGroupParams p)
		{
			if (p == null || string.IsNullOrWhiteSpace(p.groupId))
				throw new ArgumentException("groupId is required");
			if (string.IsNullOrWhiteSpace(currentFolderPath))
				throw new InvalidOperationException("currentFolderPath is empty. Call land/listAnchors first.");

			lock (markupLock)
			{
				EnsureMarkupInitializedUnsafe();

				var groupExists = currentMarkup.UserGroups
					.Any(g => string.Equals(g?.Id, p.groupId, StringComparison.OrdinalIgnoreCase));
				if (!groupExists)
				{
					Debug($"[deleteUserGroup] group not found: {p.groupId}");
					return Task.FromResult(MakeSimpleResult(false, "Группа не найдена."));
				}

				var removedMembershipsCount = currentMarkup.Memberships
					.Count(m => string.Equals(m?.GroupId, p.groupId, StringComparison.OrdinalIgnoreCase));

				currentMarkup.UserGroups = currentMarkup.UserGroups
					.Where(g => !string.Equals(g?.Id, p.groupId, StringComparison.OrdinalIgnoreCase))
					.ToList();
				currentMarkup.Memberships = currentMarkup.Memberships
					.Where(m => !string.Equals(m?.GroupId, p.groupId, StringComparison.OrdinalIgnoreCase))
					.ToList();

				PersistMarkupUnsafe("deleteUserGroup");
				Debug($"[deleteUserGroup] deleted {p.groupId} (removedMemberships={removedMembershipsCount})");
				return Task.FromResult(MakeSimpleResult(true, "Группа удалена.", removedMemberships: removedMembershipsCount));
			}
		}

		// Принимаемые префиксы groupId для memberships. user-группа проверяется
		// по существованию в UserGroups; auto-префиксы принимаются без strict-проверки —
		// если такой auto-группы сейчас нет в дереве, рендер молча проигнорирует
		// membership, и оно автоматически "оживёт", если auto-группа вернётся
		// (например, после отката переименования).
		private static readonly string[] _autoGroupIdPrefixes = new[]
		{
			"gqlType:", "gqlField:", "tsOrphans:", "tsClass:",
		};

		private static bool LooksLikeAutoGroupId(string groupId)
		{
			if (string.IsNullOrWhiteSpace(groupId))
				return false;
			foreach (var p in _autoGroupIdPrefixes)
				if (groupId.StartsWith(p, StringComparison.Ordinal))
					return true;
			return false;
		}

		private bool ValidateGroupIdUnsafe(string groupId, out string errorMessage)
		{
			errorMessage = null;
			if (string.IsNullOrWhiteSpace(groupId))
			{
				errorMessage = "Идентификатор группы пуст.";
				return false;
			}

			if (groupId.StartsWith(UserGroupIdPrefix, StringComparison.Ordinal))
			{
				var exists = currentMarkup.UserGroups
					.Any(g => string.Equals(g?.Id, groupId, StringComparison.OrdinalIgnoreCase));
				if (!exists)
				{
					errorMessage = "Пользовательская группа не найдена.";
					return false;
				}
				return true;
			}

			if (LooksLikeAutoGroupId(groupId))
				return true;

			errorMessage = $"Не распознан тип группы: {groupId}";
			return false;
		}

		[JsonRpcMethod("land/addAnchorToGroup", UseSingleObjectParameterDeserialization = true)]
		public Task<UserGroupSimpleResultV2> AddAnchorToGroupAsync(AddAnchorToGroupParams p)
		{
			if (p == null || string.IsNullOrWhiteSpace(p.anchorId) || string.IsNullOrWhiteSpace(p.groupId))
				throw new ArgumentException("anchorId and groupId are required");
			if (string.IsNullOrWhiteSpace(currentFolderPath))
				throw new InvalidOperationException("currentFolderPath is empty. Call land/listAnchors first.");

			lock (markupLock)
			{
				EnsureMarkupInitializedUnsafe();

				if (!ValidateGroupIdUnsafe(p.groupId, out var groupErr))
					return Task.FromResult(MakeSimpleResult(false, groupErr));

				// AnchorId должен соответствовать настоящему якорю (не shadow-Id)
				var anchorExists = currentMarkup.Anchors
					.Any(a => a != null
						&& string.Equals(a.NodeType, "anchor", StringComparison.OrdinalIgnoreCase)
						&& string.Equals(a.Id, p.anchorId, StringComparison.OrdinalIgnoreCase));
				if (!anchorExists)
					return Task.FromResult(MakeSimpleResult(false, "Якорь не найден в текущем снимке. Обновите панель."));

				var alreadyMember = currentMarkup.Memberships
					.Any(m => string.Equals(m?.GroupId, p.groupId, StringComparison.OrdinalIgnoreCase)
						&& string.Equals(m?.AnchorId, p.anchorId, StringComparison.OrdinalIgnoreCase));
				if (alreadyMember)
				{
					Debug($"[addAnchorToGroup] {p.anchorId} already in {p.groupId}");
					return Task.FromResult(MakeSimpleResult(true, "Якорь уже состоит в этой группе.", alreadyMember: true));
				}

				currentMarkup.Memberships.Add(new UserGroupMembership
				{
					GroupId = p.groupId,
					AnchorId = p.anchorId,
					Order = null,
					AddedAtUtc = DateTime.UtcNow,
				});

				PersistMarkupUnsafe("addAnchorToGroup");
				Debug($"[addAnchorToGroup] added {p.anchorId} to {p.groupId}");
				return Task.FromResult(MakeSimpleResult(true, "Якорь добавлен в группу.", alreadyMember: false));
			}
		}

		[JsonRpcMethod("land/removeAnchorFromGroup", UseSingleObjectParameterDeserialization = true)]
		public Task<UserGroupSimpleResultV2> RemoveAnchorFromGroupAsync(RemoveAnchorFromGroupParams p)
		{
			if (p == null || string.IsNullOrWhiteSpace(p.anchorId) || string.IsNullOrWhiteSpace(p.groupId))
				throw new ArgumentException("anchorId and groupId are required");
			if (string.IsNullOrWhiteSpace(currentFolderPath))
				throw new InvalidOperationException("currentFolderPath is empty. Call land/listAnchors first.");

			lock (markupLock)
			{
				EnsureMarkupInitializedUnsafe();

				var before = currentMarkup.Memberships.Count;
				currentMarkup.Memberships = currentMarkup.Memberships
					.Where(m => !(string.Equals(m?.GroupId, p.groupId, StringComparison.OrdinalIgnoreCase)
						&& string.Equals(m?.AnchorId, p.anchorId, StringComparison.OrdinalIgnoreCase)))
					.ToList();
				var removed = before - currentMarkup.Memberships.Count;

				if (removed == 0)
					return Task.FromResult(MakeSimpleResult(false, "Якорь не состоит в этой группе."));

				PersistMarkupUnsafe("removeAnchorFromGroup");
				Debug($"[removeAnchorFromGroup] removed {p.anchorId} from {p.groupId}");
				return Task.FromResult(MakeSimpleResult(true, "Якорь удалён из группы."));
			}
		}
	}
}
