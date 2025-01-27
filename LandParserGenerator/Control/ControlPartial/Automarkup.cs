using Land.Control.Helpers;
using Land.Control.Models;
using Land.Control.Properties;
using Land.Core;
using Land.Core.Parsing.Tree;
using Land.Markup;
using Land.Markup.Binding;
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

#pragma warning disable CA1031 // Do not catch general exception types

namespace Land.Control
{
	public partial class LandExplorerControl : UserControl, INotifyPropertyChanged
	{
		private void Estimate(
			KeyValuePair<GoFuncNode, List<GoFuncNode>> items,
			double avgMaxCallsPerResolver,
			Dictionary<string, List<int>> linesPerFuncInStruct,
			Dictionary<ParsedFile, List<int>> linesPerFuncInFile
		)
		{
			foreach (var item in items.Value)
			{
				if (item.Reciever.ToLower().Contains("unimplemented"))
				{
					item.Score -= 100; // -inf
				}
				if (item.Reciever.ToLower().Contains("resolver"))
				{
					item.Score += 1;
				}
				if (item.Name.ToLower().Contains("resolver"))
				{
					item.Score += 1;
				}
				if (items.Value.Count == 1)
				{
					item.Score += 1;
				}

				var m = Math.Min(Math.Max(1 - item.CallsCnt / avgMaxCallsPerResolver, 0), 1) / 4;
				item.Score += m;

				double m2;
				if (item.MockCallsCnt == 0 || item.CallsCnt == 0)
				{
					m2 = 1;
				}
				else
				{
					m2 = 1 - item.MockCallsCnt / (double)item.CallsCnt;
				}
				item.Score += m2;


				// учесть распределение по структурам
				var numLines = item.NumLines();
				//Debug($"{item} {numLines}");

				if (linesPerFuncInStruct.TryGetValue(item.Reciever, out List<int> value))
				{
					value.Add(numLines);
				}
				else
				{
					linesPerFuncInStruct.Add(item.Reciever, new List<int> { numLines });
				}

				if (linesPerFuncInFile.TryGetValue(item.ParsedFile, out List<int> value2))
				{
					value2.Add(numLines);
				}
				else
				{
					linesPerFuncInFile.Add(item.ParsedFile, new List<int> { numLines });
				}
			}
		}

		private void EstimateRound2(
			KeyValuePair<GoFuncNode, List<GoFuncNode>> items,
			Dictionary<string, double> covariantLinesPerFuncInStruct,
			Dictionary<ParsedFile, double> covariantLinesPerFuncInFile
		)
		{
			foreach (var item in items.Value)
			{
				item.Score += (1 - covariantLinesPerFuncInStruct[item.Reciever]) * 0.25;
				item.Score += (1 - covariantLinesPerFuncInFile[item.ParsedFile]) * 0.75;
				Debug($"{item} {1 - covariantLinesPerFuncInStruct[item.Reciever]} {1 - covariantLinesPerFuncInFile[item.ParsedFile]}");
			}
		}

		private double CompareNames(string go, string graphql)
		{
			go = go.ToLower();
			graphql = graphql.ToLower();
			if (go == graphql) return 1;
			if (go.Contains(graphql)) return 0.75;
			return 0;
		}

		private void Fill()
		{
			var gqlFiles = Editor.GetAllFiles("graphql");

			var gqlFuncs = new Dictionary<string, List<ConcernPointCandidate>>();
			var gqlTypes = new Dictionary<string, List<ConcernPointCandidate>>();
			var groups = new Dictionary<string, List<Concern>>(); // functionality name -> group (concern) in markup
			var gqlTypesConcernCandidate = new Dictionary<string, ExistingConcernPointCandidate>();

			var d = new ResourceStats();

			foreach (var file in gqlFiles)
			{
				d.Start();
				var pFile = LogFunction(() => GetParsed(file), true, false);
				d.Stop(ref d.ParseGraphql);

				if (pFile == null)
				{
					// todo: handle
					continue;
				}


				d.Start();
				var funcsAndTypes = GetGraphqlFuncsAndTypes(pFile, gqlFuncs, gqlTypes);
				foreach (var c in funcsAndTypes.Funcs.OfType<ExistingConcernPointCandidate>())
				{
					var typeName = c.Node.Parent.Children[1].ToString().ToLower().Replace("id: ", "");
					//Debug($"belongs to {typeName}");

					var name = c.Node.Children.First().ToString();
					var group = MarkupManager.AddConcern(name);
					var groupName = name.ToLower().Replace("id: ", "");

					group.GqlTypeName = typeName;

					if (groups.TryGetValue(groupName, out var elems))
						elems.Add(group);
					else
						groups.Add(groupName, new List<Concern>() { group });

					MarkupManager.AddConcernPoint(
						c.Node,
						null,
						pFile,
						c.ViewHeader,
						"graphql schema",
						group,
						false
					);
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
				d.Stop(ref d.AddGraphqlConcern);
			}

			Debug($"got {gqlFuncs.Count} gql functions");
			Debug("looking for go resolvers...");

			var goFiles = Editor.GetAllFiles("go");

			Debug($"looking for go resolvers ({goFiles.Count()} files)...");

			var resolvers = new Dictionary<GoFuncNode, List<GoFuncNode>>();
			var potentialResolvers = new Dictionary<GoFuncNode, List<GoFuncNode>>();
			var maxCallsPerResolver = new Dictionary<string, int>();

			Stopwatch watch;

			foreach (var file in goFiles)
			{
				watch = Stopwatch.StartNew();
				var pFile = GetParsed(file, d);
				watch.Stop();
				d.ParseGoTotal += watch.ElapsedMilliseconds;

				d.Start();
				VisitGoResolversV2(pFile, gqlFuncs, gqlTypes, resolvers, potentialResolvers, maxCallsPerResolver);
				d.Stop(ref d.VisitGo);
			}

			Debug($"got {potentialResolvers.Count()} potential resolvers");
			Debug($"matching ({resolvers.Count()} resolvers)...");

			var avgMaxCallsPerResolver = maxCallsPerResolver.Values.Average();


			d.Start();
			var linesPerFuncInStruct = new Dictionary<string, List<int>>(); // структура => кол-во строк в первой функции, кол-во строк во второй ф-и, в третьей, ...
			var linesPerFuncInFile = new Dictionary<ParsedFile, List<int>>(); // File => кол-во строк в первой функции, кол-во строк во второй ф-и, в третьей, ...


			foreach (var items in resolvers)
			{
				Estimate(items, avgMaxCallsPerResolver, linesPerFuncInStruct, linesPerFuncInFile);
			}
			foreach (var items in potentialResolvers)
			{
				Estimate(items, avgMaxCallsPerResolver, linesPerFuncInStruct, linesPerFuncInFile);
			}

			var covariantLinesPerFuncInStruct = new Dictionary<string, double>();
			foreach (var elem in linesPerFuncInStruct)
			{
				var vals = elem.Value;
				var m = vals.Average();
				var sigma = Math.Sqrt(vals.Average(x => (x - m) * (x - m)));
				var cv = sigma / m;
				if (cv < 0 || cv > 5) throw new Exception("covariant incorrect!");
				covariantLinesPerFuncInStruct[elem.Key] = cv;
			}
			var covariantLinesPerFuncInFile = new Dictionary<ParsedFile, double>();
			foreach (var elem in linesPerFuncInFile)
			{
				var vals = elem.Value;
				var m = vals.Average();
				var sigma = Math.Sqrt(vals.Average(x => (x - m) * (x - m)));
				var cv = sigma / m;
				if (cv < 0 || cv > 5) throw new Exception("covariant incorrect!");
				covariantLinesPerFuncInFile[elem.Key] = cv;
			}

			foreach (var items in resolvers)
			{
				EstimateRound2(items, covariantLinesPerFuncInStruct, covariantLinesPerFuncInFile);
			}
			foreach (var items in potentialResolvers)
			{
				EstimateRound2(items, covariantLinesPerFuncInStruct, covariantLinesPerFuncInFile);
			}

			var resolversPerReciever = new Dictionary<string, int>();
			foreach (var item in resolvers)
			{
				foreach (var cand in item.Value.OrderByDescending(x => x.Score))
				{
					//var max = item.Value.OrderByDescending(x => x.Score).First();
					var c = (ConcernPointCandidate)new ExistingConcernPointCandidate(cand.Node, cand.ParsedFile);
					c.NormalizedName = cand.Name;

					resolversPerReciever.TryGetValue(cand.Reciever, out var cnt);
					resolversPerReciever[cand.Reciever] = cnt + 1;

					if (groups[c.NormalizedName].Count > 1)
					{
						Debug($"ambiguous func {c.NormalizedName} {groups[c.NormalizedName].Count}");
					}
					foreach (var group in groups[c.NormalizedName])
					{
						var gqlTypeName = group.GqlTypeName;
						var m = (cand.Score + CompareNames(cand.Reciever, gqlTypeName)) / 4;
						MarkupManager.AddConcernPoint(
							(c as ExistingConcernPointCandidate).Node,
							null,
							cand.ParsedFile,
							c.ViewHeader + m.ToString("0.00"),
							null,
							group,
							false,
							m// ресивер = тип в графкл => это наш кандидат

						);
					}
				}

			}

			foreach (var item in potentialResolvers)
			{
				var max = item.Value.Where(x => resolversPerReciever.ContainsKey(x.Reciever) && resolversPerReciever[x.Reciever] > 1).
					OrderByDescending(x => x.Score).
					FirstOrDefault();

				if (max == null)
					continue;

				Debug($"found potential resolver {max}");

				var c = new ExistingConcernPointCandidate(max.Node);
				c.NormalizedName = max.Name;

				var name = c.NormalizedName;
				var group = MarkupManager.AddConcern(name);
				// adding golang
				MarkupManager.AddConcernPoint(
						c.Node,
						null,
						max.ParsedFile,
						c.ViewHeader + "P" + max.NScore.ToString("0.00"),
						null,
						group,
						false,
						max.Score / 4
				);

				c = gqlTypesConcernCandidate[name];
				//adding gql
				MarkupManager.AddConcernPoint(
						c.Node,
						null,
						c.ParsedFile,
						c.ViewHeader,
						"graphql schema",
						group,
						false
				);
			}


			MarkupManager.CheckMarkup();


			d.Stop(ref d.AddGoConcern);

			SetStatus(d.ToString(), ControlStatus.Success);


			// debug
			var bad = 0;
			/*foreach (var group in groups)
			{
				if (group.Value.Elements.Count == 2)
				{
					group.Value.Name = "";
				}
				else bad++;
			}*/
			//SetStatus($"bad concerns: {bad}", ControlStatus.Success);
		}





		public GraphqlConcernPointCandidates GetGraphqlFuncsAndTypes(
			ParsedFile file,
			Dictionary<string, List<ConcernPointCandidate>> gqlFuncs,
			Dictionary<string, List<ConcernPointCandidate>> gqlTypes)
		{
			var nodes = MarkupManager.GetGraphqlFuncNodes(file.Root);
			var listFuncs = new List<ConcernPointCandidate>(nodes.Funcs.Count);
			foreach (var item in nodes.Funcs)
			{
				var c = (ConcernPointCandidate)new ExistingConcernPointCandidate(item, file);
				listFuncs.Add(c);
				if (gqlFuncs.TryGetValue(c.NormalizedName, out var candidates))
					candidates.Add(c);
				else
					gqlFuncs.Add(c.NormalizedName, new List<ConcernPointCandidate>() { c });
			}

			var listTypes = new List<ConcernPointCandidate>(nodes.Types.Count);
			foreach (var item in nodes.Types)
			{
				var c = (ConcernPointCandidate)new ExistingConcernPointCandidate(item, file);
				listTypes.Add(c);
				if (gqlTypes.TryGetValue(c.NormalizedName, out var candidates))
					candidates.Add(c);
				else
					gqlTypes.Add(c.NormalizedName, new List<ConcernPointCandidate>() { c });
			}
			return new GraphqlConcernPointCandidates(listFuncs, listTypes);
		}


		/// <summary>
		///  Кандидаты (пока что) на резолверы. Reciever -> go func
		/// </summary>
		public void VisitGoResolvers(
			ParsedFile file,
			Dictionary<string, List<ConcernPointCandidate>> graphqlFuncs,
			Dictionary<string, List<ConcernPointCandidate>> graphqlTypes,
			Dictionary<GoFuncNode, List<GoFuncNode>> resFuncs,
			Dictionary<GoFuncNode, List<GoFuncNode>> resTypes,
			Dictionary<string, int> funcsPerReciever, // функции-тезки с разными резолверами
			Dictionary<string, int> funcsPerPackage
			)
		{
			var nodes = MarkupManager.GetGoNodes(file.Root);
			var types = GetGoTypes(nodes.Types);
			VisitGoResolverCandidates(file, nodes.Funcs, types, graphqlFuncs, graphqlTypes, resFuncs, resTypes, funcsPerReciever, funcsPerPackage);
		}
		/// <summary>
		/// Кандидаты на резолверы (у кого есть аргумент struct)
		/// </summary>
		public void VisitGoResolverCandidates(
			ParsedFile file,
			LinkedList<Node> nodes,
			Dictionary<string, GoTypeNode> goTypes,
			Dictionary<string, List<ConcernPointCandidate>> graphqlFuncs,
			Dictionary<string, List<ConcernPointCandidate>> graphqlTypes,
			Dictionary<GoFuncNode, List<GoFuncNode>> resFuncs,
			Dictionary<GoFuncNode, List<GoFuncNode>> resTypes,
			Dictionary<string, int> funcsPerReciever, // функции-тезки с разными резолверами
			Dictionary<string, int> funcsPerPackage // функции-тезки с разными резолверами
			)
		{
			var package = file.Root.Children[1].Children[0].ToString().Replace("ID: ", "");
			foreach (var node in nodes)
			{
				var candidate = (GoFuncNode)null;

				// func, f_reciever, f_name, f_args, f_returns

				var idx = 1;
				Node nextChild() { return node.Children[idx++]; }

				// f_reciever
				var child = nextChild();
				if (child.ToString() != "f_reciever" || child.Children.Count() == 0) continue;
				var reciver = child.Children.Last().Children.First().ToString().Replace("ID: ", "");


				// f_name
				child = nextChild();
				var name = child.ToString().Replace("f_name: ", "").Replace("_", "").ToLower();

				var (isFunc, isType) = (graphqlFuncs.ContainsKey(name), graphqlTypes.ContainsKey(name));
				if (!isFunc && !isType) continue;

				// f_args
				child = nextChild();
				var args = child.Children.Where(x => x.ToString().StartsWith("f_arg: "));

				var weight = 0;
				if (isType && args.Count() != 1) continue; // type having ctx arg is func

				switch (args.Count())
				{
					case 0:
						if (isFunc) continue;
						break;
					case 1:
					case 2:
						weight = 2;
						foreach (var arg in args) // always 1 or 2 args for resolver
						{
							var argType = arg.ToString().Replace("f_arg: ", "");
							if (argType == "anon_struct")
							{
								weight = 3;
								break;
							}
							if (goTypes.TryGetValue(argType, out var goType))
							{
								if (goType.GoType is StructType)
								{
									// this struct has been seen earlier
									weight = 3;
									break;
								}
							}
						}
						break;
					default:
						weight = 1; // странная библиотека, позволяющая аргументы записывать в линейном виде
						break;

				}


				// f_returns
				child = nextChild();
				if (child.Children.Count() == 0) continue;

				candidate = new GoFuncNode(file, node, reciver, package, name, 0, 0, 0);

				if (isType && !isFunc)
				{
					if (resTypes.TryGetValue(candidate, out var types))
						types.Add(candidate);
					else
						resTypes.Add(candidate, new List<GoFuncNode>() { candidate });
					continue; // doesn't consider types in weights 
				}


				if (funcsPerReciever.TryGetValue(reciver, out _))
					funcsPerReciever[reciver] += weight;
				else
					funcsPerReciever.Add(reciver, weight);

				if (funcsPerPackage.TryGetValue(package, out _))
					funcsPerPackage[package] += weight;
				else
					funcsPerPackage.Add(package, weight);

				if (resFuncs.TryGetValue(candidate, out var funcs))
					funcs.Add(candidate);
				else
					resFuncs.Add(candidate, new List<GoFuncNode>() { candidate });
			}
		}

		public void VisitGoResolversV2(
			ParsedFile file,
			Dictionary<string, List<ConcernPointCandidate>> graphqlFuncs,
			Dictionary<string, List<ConcernPointCandidate>> graphqlTypes,
			Dictionary<GoFuncNode, List<GoFuncNode>> resolvers,
			Dictionary<GoFuncNode, List<GoFuncNode>> potentialResolvers,
			Dictionary<string, int> maxCallsPerResolver
			)
		{
			var nodes = MarkupManager.GetGoNodes(file.Root);
			var types = GetGoTypes(nodes.Types);
			VisitGoResolverCandidatesV2(file, nodes.Funcs, types, graphqlFuncs, graphqlTypes, resolvers, potentialResolvers, maxCallsPerResolver);
		}

		public int VisitGoResolverBodyCalls(Node root)
		{
			if (root == null)
			{
				return 0;
			}
			var res = 0;
			if (root.ToString() == "call")
			{
				res++;
			}
			foreach (var child in root.Children)
			{
				res += VisitGoResolverBodyCalls(child);
			}
			return res;
		}

		public int VisitGoResolverBodyMockCalls(Node root)
		{
			if (root == null)
			{
				return 0;
			}

			var res = 0;
			foreach (var child in root.Children)
			{
				if (child.ToString() == "call")
				{
					// мы учитываем только те идентификаторы Call/Called/Get, что были найдены внутри вызова (call)
					res += visitGoResolverBodyMockCalls(child);
				}
				else
				{
					res += VisitGoResolverBodyMockCalls(child);
				}
			}
			return res;
		}

		public int visitGoResolverBodyMockCalls(Node root)
		{
			if (root == null)
			{
				return 0;
			}

			var callId = root.ToString().ToLower();
			if (callId == "id: get" || callId == "id: call" || callId == "id: called" || callId == "id: invoke")
			{
				return 1;
			}

			var res = 0;
			foreach (var child in root.Children)
			{
				res += visitGoResolverBodyMockCalls(child);
			}
			return res;
		}

		public (int, int) VisitGoResolverBodyControls(Node root)
		{
			if (root == null)
			{
				return (0, 0);
			}
			var anonCalls = 0;
			var controls = 0;
			if (root.ToString() == "anon_func_call")
			{
				anonCalls++;
			}
			if (root.ToString() == "if" || root.ToString() == "for" || root.ToString() == "switch" || root.ToString() == "select")
			{
				controls++;
			}
			foreach (var child in root.Children)
			{
				var tmp = VisitGoResolverBodyControls(child);
				anonCalls += tmp.Item1;
				controls += tmp.Item2;
			}
			return (anonCalls, controls);
		}

		public void VisitGoResolverCandidatesV2(
			ParsedFile file,
			LinkedList<Node> nodes,
			Dictionary<string, GoTypeNode> goTypes,
			Dictionary<string, List<ConcernPointCandidate>> graphqlFuncs,
			Dictionary<string, List<ConcernPointCandidate>> graphqlTypes,
			Dictionary<GoFuncNode, List<GoFuncNode>> resolvers,
			Dictionary<GoFuncNode, List<GoFuncNode>> potentialResolvers,
			Dictionary<string, int> maxCallsPerResolver
			)
		{
			var package = file.Root.Children[1].Children[0].ToString().Replace("ID: ", "");
			foreach (var node in nodes)
			{
				//node
				// func, f_reciever, f_name, f_args, f_returns, { body }

				var idx = 1;
				Node nextChild() { return node.Children[idx++]; }

				// f_reciever
				var child = nextChild();
				if (child.ToString() != "f_reciever" || child.Children.Count() == 0)
					continue;
				var reciver = child.Children.Last().Children.First().ToString().Replace("ID: ", "");


				// f_name
				child = nextChild();
				var name = child.ToString().Replace("f_name: ", "").Replace("_", "");
				if (char.IsLower(name[0]))
				{
					// private method
					continue;
				}
				name = name.ToLower();


				if (!graphqlFuncs.ContainsKey(name) && !graphqlTypes.ContainsKey(name))
				{
					continue;
				}

				// f_args
				child = nextChild();
				var args = child.Children.Where(x => x.ToString().StartsWith("f_arg: "));
				if (args.Count() == 0)
				{
					continue;
				}


				// body
				idx += 2;
				var l = node.Children[idx].Location;
				var txt = file.Text.Substring(l.Start.Offset, l.End.Offset - l.Start.Offset + 1);

				var parsedFileCalls = ParseFragment(".pure_calls", file.Name, txt);
				var callsCnt = VisitGoResolverBodyCalls(parsedFileCalls.Root);

				var mockCalls = VisitGoResolverBodyMockCalls(parsedFileCalls.Root);

				var parsedFileControls = ParseFragment(".controls", file.Name, txt);
				var controlsCntRes = VisitGoResolverBodyControls(parsedFileControls.Root);
				callsCnt += controlsCntRes.Item1;
				var controlsCnt = controlsCntRes.Item2;

				if (callsCnt + controlsCnt == 0)
				{
					Debug($"non-trivial resolver {name}");
					continue;
				}


				maxCallsPerResolver.TryGetValue(reciver, out int val);
				if (callsCnt > val)
				{
					maxCallsPerResolver[reciver] = callsCnt;
				}


				if (graphqlFuncs.ContainsKey(name))
				{
					var candidate = new GoFuncNode(file, node, reciver, package, name, callsCnt, controlsCnt, mockCalls);
					if (resolvers.TryGetValue(candidate, out var funcs))
					{
						Debug($"Add new resolver {name} for reciever {package}.{reciver}");
						funcs.Add(candidate);
					}
					else
					{
						Debug($"Add one more resolver {name} for reciever {package}.{reciver}");
						resolvers.Add(candidate, new List<GoFuncNode>() { candidate });
					}
					continue;
				}
				if (graphqlTypes.ContainsKey(name))
				{
					var candidate = new GoFuncNode(file, node, reciver, package, name, callsCnt, controlsCnt, mockCalls);
					if (potentialResolvers.TryGetValue(candidate, out var funcs))
					{
						Debug($"Add new potential resolver {name} for reciever {package}.{reciver}");
						funcs.Add(candidate);
					}
					else
					{
						Debug($"Add one more potential resolver {name} for reciever {package}.{reciver}");
						potentialResolvers.Add(candidate, new List<GoFuncNode>() { candidate });
					}
					continue;
				}

			}
		}

		private void Debug(string msg)
		{
			System.Diagnostics.Debug.WriteLine("LOG🔔 " + msg);
		}

		/// <summary>
		/// Находит все Go объявленные типы в файле (пока только структуры)
		/// </summary>
		public Dictionary<string, GoTypeNode> GetGoTypes(LinkedList<Node> nodes)
		{
			var types = new Dictionary<string, GoTypeNode>();

			foreach (var typeDef in nodes)
			{
				var node = typeDef.Children.FirstOrDefault(x => x.ToString() == "struct_type");
				if (node == null) continue;

				var stName = node.Children.First().ToString().Replace("ID: ", "");
				var st = new StructType(stName);

				node = node.Children?.FirstOrDefault(x => x.ToString() == "anon_struct")?.
					Children?.FirstOrDefault(x => x.ToString() == "struct_content");
				if (node == null) continue;


				bool onlyOneField = node.Children.Count <= 2;
				string lastType = "";
				string lastDelim = "";
				node.Children.Reverse();
				foreach (var item in node.Children)
				{
					if (item.ToString() == "struct_delim")
					{
						lastDelim = item.Children[0].ToString().Contains(",") ? "," : "\n";
						continue;
					}


					var goTypes = item.Children.Where(x => x.ToString() == "go_type");
					if (goTypes.Count() > 1 || lastDelim == "\n" || onlyOneField) // embeded structs
					{
						lastType = goTypes.Last().Children.First(x => x.ToString() != "arr_ptr").ToString().Replace("ID: ", "");
					}

					if (types.TryGetValue(lastType, out var goType))
					{
						st.Fields.Add(goType.GoType);
					}
					else
					{
						st.Fields.Add(new SimpleType(lastType));
					}
				}

				if (types.ContainsKey(stName))
				{
					SWF.MessageBox.Show($"duplicated type! {stName}");
				}
				else
				{
					types.Add(stName, new GoTypeNode(node, st));
				}
			}

			return types;
		}

	}
}