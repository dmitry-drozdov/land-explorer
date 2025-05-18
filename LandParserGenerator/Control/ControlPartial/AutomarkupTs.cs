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


namespace Land.Control
{
	public partial class LandExplorerControl : UserControl, INotifyPropertyChanged
	{
		void FillTs()
		{
			var tsFiles = Editor.GetAllFiles("ts");
			foreach (var tsFile in tsFiles)
			{
				var pFile = GetParsed(tsFile);
				var tsNodes = new TsNodes(new List<Node>());
				MarkupManager.GetTsNodes(pFile.Root, tsNodes);
				if (tsNodes.Funcs.Count != 19)
				{
					Debug($"{tsNodes.Funcs.Count} funcs found in {tsFile}");
				}
			}
		}
	}
}