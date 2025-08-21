using Jaeger;
using Land.Control;
using Land.Core.Parsing.Tree;
using OpenTracing.Util;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using VPTree;

namespace Land.Markup
{
	public class MarkupGraphql
	{
		public List<MethodAnchor> anchors = new List<MethodAnchor>();
		private Dictionary<Guid, MethodAnchor> _anchorByConcernId = new Dictionary<Guid, MethodAnchor>();
		private Rebinder _rebinder;
		public MarkupGraphql() { }

		public void AddAnchor(Node n, Guid concernId)
		{
			var typeName = n.Parent.Children[1].ToString().Replace("id: ", "");
			var name = n.Children.First().ToString().Replace("id: ", "");

			var args = n.Children.Skip(1).
				TakeWhile(x => x.Type == "func_arg").
				Select(x => new Tuple<string, string>(
					x.Children[0].Children[1].Children.First(y => y.ToString() != "LSB: [").ToString().Replace("id: ", ""), // type
					x.Children[0].Children[0].ToString().Replace("id: ", "") // name
				));

			var returnType = n.Children.Last().Children.First(y => y.ToString() != "LSB: [").ToString().Replace("id: ", "");

			var anchor = MethodAnchor.FromRaw(n.Id.ToString(), name, args, new List<string> { returnType }, typeName);
			anchors.Add(anchor);
			_anchorByConcernId[concernId] = anchor;
		}

		public void CreateRebinder()
		{
			var weights = new Dist.Weights();
			_rebinder = new Rebinder(anchors, weights, Tracing.Tracer as GlobalTracer);
			using (var scope = Tracing.Tracer.BuildSpan("SaveJson").StartActive())
				AnchorsIO.SaveJson("anchors.json", anchors);
		}

		public List<MatchResult> RebindToOld(IEnumerable<MethodAnchor> newAnchors, int k = 3, double tau = 0.18, double margin = 0.02)
		{
			var engine = new RebindEngine(_rebinder, anchors, new Dist.Weights());
			using (var scope = Tracing.Tracer.BuildSpan("Rebind").StartActive())
				return engine.Rebind(newAnchors, k, tau, margin);
		}

	}
}
