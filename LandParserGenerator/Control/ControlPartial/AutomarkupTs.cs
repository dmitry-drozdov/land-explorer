using Land.Control.Helpers;
using Land.Control.Models;
using Land.Control.Properties;
using Land.Core;
using Land.Core.Parsing.Tree;
using Land.Markup;
using Land.Markup.Binding;
using Land.Markup.CoreExtension;
using Land.Markup.Tree;
using Microsoft.Win32;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Xml;
using System.Xml.Linq;
using static System.Net.WebRequestMethods;
using SWF = System.Windows.Forms;


namespace Land.Control
{
	public partial class LandExplorerControl : UserControl, INotifyPropertyChanged
	{
		void FillTs()
		{
			Tracing.Init();
			using (var scope = Tracing.Tracer.BuildSpan("FillTs").StartActive())
				FillTsHelp();
		}
		void FillTsHelp()
		{

			var gqlFiles = Editor.GetAllFiles("graphql");

			var gqlFuncs = new Dictionary<string, List<ConcernPointCandidate>>();
			var gqlTypes = new Dictionary<string, List<ConcernPointCandidate>>();
			var groups = new Dictionary<string, List<Concern>>(); // functionality name -> group (concern) in markup
			var gqlTypesConcernCandidate = new Dictionary<string, ExistingConcernPointCandidate>();

			var d = new ResourceStats();

			foreach (var file in gqlFiles)
			{
				Debug($"parsing gql {file}");
				d.Start("parseGQL");
				var pFile = LogFunction(() => GetParsed(file), true, false);
				d.Stop("parseGQL");
				Debug($"parsed gql {file}");

				if (pFile == null)
				{
					// todo: handle
					continue;
				}


				d.Start("markGQL");
				var funcsAndTypes = GetGraphqlFuncsAndTypes(pFile, gqlFuncs, gqlTypes);
				var visitorCache = new Dictionary<string, GroupNodesByTypeVisitor>();
				var ancestorToSiblingsCache = new Dictionary<Node, SiblingsContextConstructionCache>();
				var coll = funcsAndTypes.Funcs.OfType<ExistingConcernPointCandidate>().ToList();
				for (int i = 0; i < coll.Count; i++)
				{
					var c = coll[i];
					var typeName = c.Node.Parent.Children[1].ToString().ToLower().Replace("id: ", "");
					//Debug($"belongs to {typeName}");

					var name = c.Node.Children.First().ToString();
					var group = MarkupManager.AddConcern(name, refresh: i == coll.Count - 1);
					var groupName = name.ToLower().Replace("id: ", "");

					group.GqlTypeName = typeName;

					if (groups.TryGetValue(groupName, out var elems))
						elems.Add(group);
					else
						groups.Add(groupName, new List<Concern>() { group });

					Debug($"start adding concern {i + 1} / {coll.Count}");


					using (var scope = Tracing.Tracer.BuildSpan("AddConcernPoint").StartActive())
						MarkupManager.AddConcernPoint(
							c.Node,
							null,
							pFile,
							c.ViewHeader,
							"graphql schema",
							group,
							false,
							0,
							ancestorToSiblingsCache,
							visitorCache,
							refresh: i == coll.Count - 1
						);

					//Debug("end adding concern");
				}

				foreach (var c in funcsAndTypes.Types.OfType<ExistingConcernPointCandidate>())
				{
					var name = c.Node.Children.First().ToString();
					var groupName = name.ToLower().Replace("id: ", "");
					if (!gqlTypesConcernCandidate.ContainsKey(groupName))
					{
						gqlTypesConcernCandidate.Add(groupName, c);
					}
				}
				d.Stop("markGQL");
			}

			Debug($"got {gqlFuncs.Count} gql functions");
			Debug("looking for ts resolvers...");

			var resolvers = new Dictionary<TsFuncNode, List<TsFuncNode>>();

			var tsFiles = Editor.GetAllFiles("ts");
			Debug("parsed ts");
			foreach (var tsFile in tsFiles)
			{
				VisitTsNode(tsFile, resolvers, gqlFuncs, gqlTypes);
			}
			Debug("visited ts");

			foreach (var item in resolvers)
			{
				foreach (var resolver in item.Value)
				{
					double metric = 0;
					if (resolver.ClassName.ToLower().Contains("resolver"))
						metric += 1;


					Concern target = null;
					if (!groups.ContainsKey(resolver.Name))
					{
						var c = gqlTypesConcernCandidate[resolver.Name];
						var name = c.Node.Children.First().ToString();
						var group = MarkupManager.AddConcern(name);
						//var groupName = name.ToLower().Replace("id: ", "");
						groups.Add(resolver.Name, new List<Concern>() { group });

						Debug($"Adding gql schema {c.ParsedFile.Name} {c.ViewHeader}");
						MarkupManager.AddConcernPoint(
							c.Node,
							null,
							c.ParsedFile,
							c.ViewHeader,
							"graphql schema",
							group,
							false,
							refresh: false
						);
						target = group;
					}
					else
					{
						target = groups[resolver.Name][0];
					}

					// TODO здесь надо сравнивать с именем ТИПА графкл, а не ПОЛЯ
					if (resolver.ClassName.ToLower() == (target.Name.ToLower().Replace("id: ", "")))
					{
						metric += 1;
					}
					else if (resolver.ClassName.ToLower().Contains(target.Name.ToLower().Replace("id: ", "")))
					{
						metric += 0.75;
					}

					Debug($"Adding resolver {resolver.ParsedFile.Name} {resolver.ToString()}");
					MarkupManager.AddConcernPoint(
						resolver.Node,
						null,
						resolver.ParsedFile,
						resolver.ToString(),
						"",
						target,
						false,
						metric,
						refresh: false
					);

				}
			}

			MarkupManager.Refresh();
			MarkupManager.CheckMarkup();
		}

		void VisitTsNode(
			string tsFile,
			 Dictionary<TsFuncNode, List<TsFuncNode>> resolvers,
			 Dictionary<string, List<ConcernPointCandidate>> gqlFuncs,
			  Dictionary<string, List<ConcernPointCandidate>> gqlTypes
		)
		{
			var pFile = GetParsed(tsFile);
			var tsNodes = MarkupManager.GetTsNodes(pFile.Root);

			//Debug($"{tsNodes.Funcs.Count} funcs found in {tsFile}");
			foreach (var kv in tsNodes.FuncsPerClass)
			{
				foreach (var node in kv.Value)
				{
					var name = node.Children[0].ToString().Replace("ID: ", "").ToLower();
					//Debug($"{name} funcs found in {tsFile}");
					var args = node.Children[1].Children.Select(x => x.ToString().Replace("arg", "")).Where(x => x != "").ToList();

					if (!gqlFuncs.ContainsKey(name) && !gqlTypes.ContainsKey(name))
						continue;

					var candidate = new TsFuncNode(pFile, node, name, kv.Key, args);
					if (!resolvers.ContainsKey(candidate))
					{
						resolvers.Add(candidate, new List<TsFuncNode>());
					}
					resolvers[candidate].Add(candidate);
				}
			}

		}
	}
}