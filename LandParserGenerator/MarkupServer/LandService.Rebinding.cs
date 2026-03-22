using System;
using System.Collections.Generic;
using System.Linq;
using VPTree;

namespace MarkupServer
{
	public partial class LandService
	{
		private sealed class AnchorContext
		{
			public string Id { get; set; }
			public string Language { get; set; }
			public string Family { get; set; }
			public string AnchorKind { get; set; }
			public string ProfileKey { get; set; }
			public int StartOffset { get; set; }
			public int EndOffset { get; set; }
			public string HeaderCoreNorm { get; set; }
			public string AncestorPathNorm { get; set; }
			public string InnerSketchNorm { get; set; }
			public List<Arg> HeaderArgs { get; set; } = new();
			public int OrdinalInParent { get; set; }
			public Dictionary<string, double> SiblingBag { get; set; }
		}

		private static string InferAnchorFamilyFromKind(string anchorKind)
		{
			if (string.Equals(anchorKind, AnchorKindGqlField, StringComparison.OrdinalIgnoreCase)
				|| string.Equals(anchorKind, AnchorKindTs, StringComparison.OrdinalIgnoreCase))
				return AnchorFamilyCallable;

			if (string.Equals(anchorKind, AnchorKindGqlType, StringComparison.OrdinalIgnoreCase)
				|| string.Equals(anchorKind, AnchorKindGqlInput, StringComparison.OrdinalIgnoreCase)
				|| string.Equals(anchorKind, AnchorKindGqlInterface, StringComparison.OrdinalIgnoreCase))
				return AnchorFamilyRecord;

			return null;
		}

		private static string EnsureAnchorFamily(TreeNode node)
		{
			if (node == null)
				return null;

			if (string.IsNullOrWhiteSpace(node.AnchorFamily))
				node.AnchorFamily = InferAnchorFamilyFromKind(node.AnchorKind);

			return node.AnchorFamily;
		}

		private static string GetProfileKey(string language, string family, string anchorKind)
			=> $"{(language ?? "").Trim().ToLowerInvariant()}:{(family ?? "").Trim()}:{(anchorKind ?? "").Trim()}";

		private static string GetProfileKey(TreeNode node)
		{
			if (node == null)
				return null;

			var family = EnsureAnchorFamily(node);
			if (string.IsNullOrWhiteSpace(node.Language) || string.IsNullOrWhiteSpace(family) || string.IsNullOrWhiteSpace(node.AnchorKind))
				return null;

			return GetProfileKey(node.Language, family, node.AnchorKind);
		}

		private static bool CanParticipateInRebinding(TreeNode node)
		{
			if (node == null || !string.Equals(node.NodeType, "anchor", StringComparison.OrdinalIgnoreCase))
				return false;

			return !string.IsNullOrWhiteSpace(GetProfileKey(node));
		}

		private static Dist.Weights GetWeightsForProfileKey(string profileKey)
		{
			if (string.IsNullOrWhiteSpace(profileKey))
				return new Dist.Weights();

			if (profileKey.Contains($":{AnchorFamilyCallable}:", StringComparison.OrdinalIgnoreCase))
				return new Dist.Weights();

			return new Dist.Weights
			{
				NameW = 0.25,
				ArgsW = 0.00,
				ReturnsW = 0.45,
				ParentW = 0.05,
				NeighW = 0.25,
				NameScale = 8,
				TypeScale = 16,
				ArgNameScale = 8,
				ReturnsScale = 16,
				ReceiverScale = 8,
			};
		}

		private AnchorContext BuildAnchorContext(TreeNode node)
		{
			if (!CanParticipateInRebinding(node))
				return null;

			return new AnchorContext
			{
				Id = node.Id,
				Language = node.Language,
				Family = EnsureAnchorFamily(node),
				AnchorKind = node.AnchorKind,
				ProfileKey = GetProfileKey(node),
				StartOffset = node.StartOffset ?? 0,
				EndOffset = node.EndOffset ?? 0,
				HeaderCoreNorm = node.MethodNameNorm ?? "",
				AncestorPathNorm = node.ParentNameNorm ?? "",
				InnerSketchNorm = node.ReturnTypeNorm ?? "",
				HeaderArgs = (node.Args ?? new List<Arg>())
					.Select(x => new Arg { TypeNorm = x.TypeNorm, NameNorm = x.NameNorm })
					.ToList(),
				OrdinalInParent = node.OrdinalInParent ?? 0,
				SiblingBag = node.NeighborBag != null ? new Dictionary<string, double>(node.NeighborBag, StringComparer.Ordinal) : null,
			};
		}

		private static MethodAnchor ToLegacyMethodAnchor(AnchorContext ctx)
		{
			if (ctx == null)
				return null;

			return new MethodAnchor
			{
				Id = ctx.Id,
				StartOffset = ctx.StartOffset,
				EndOffset = ctx.EndOffset,
				MethodNameNorm = ctx.HeaderCoreNorm,
				ParentNameNorm = ctx.AncestorPathNorm,
				ReturnTypeNorm = ctx.InnerSketchNorm,
				Args = (ctx.HeaderArgs ?? new List<Arg>())
					.Select(x => new MethodAnchor.Arg { TypeNorm = x.TypeNorm, NameNorm = x.NameNorm })
					.ToList(),
				OrdinalInParent = ctx.OrdinalInParent,
				NeighborBag = ctx.SiblingBag != null ? new Dictionary<string, double>(ctx.SiblingBag, StringComparer.Ordinal) : null,
			};
		}

		private static double AnchorContextDistance(AnchorContext a, AnchorContext b, Dist.Weights weights)
		{
			return Dist.AnchorDistance(ToLegacyMethodAnchor(a), ToLegacyMethodAnchor(b), weights);
		}
	}
}
