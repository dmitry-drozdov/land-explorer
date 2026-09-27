using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace MarkupServer.Tests;

/// <summary>
/// Транспорт --stdio: stdout сервера — канал JSON-RPC, в нём не должно быть ничего, кроме кадров протокола.
/// Раньше генератор парсеров LanD при land/initialize печатал в stdout служебные строки («declared new manual pair …»,
/// «BALANCED»), они шли перед ответом, и клиент (vscode-jsonrpc) ждал ответа вечно.
/// </summary>
[TestClass]
public class StdioTransportTests
{
	[TestMethod]
	public void Stdout_Carries_Only_JsonRpc_Frames()
	{
		var baseDir = AppContext.BaseDirectory;
		var exe = Path.Combine(baseDir, OperatingSystem.IsWindows() ? "MarkupServer.exe" : "MarkupServer");
		var grammars = Path.Combine(baseDir, "grammars");
		Assert.IsTrue(File.Exists(exe), "server executable next to the tests: " + exe);

		var psi = new ProcessStartInfo(exe, "--stdio")
		{
			WorkingDirectory = grammars,          // при старте сервер читает graphql.land по относительному пути
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true,
		};
		using var proc = Process.Start(psi)!;
		var stderr = new StringBuilder();
		proc.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (stderr) stderr.AppendLine(e.Data); };
		proc.BeginErrorReadLine();

		try
		{
			var body = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new
			{
				jsonrpc = "2.0",
				id = 1,
				method = "land/initialize",
				@params = new
				{
					protocolVersion = 2,
					offsetEncoding = "utf-16",
					graphqlParserPath = Path.Combine(grammars, "graphql.land"),
					typescriptParserPath = Path.Combine(grammars, "ts_resolvers.land"),
				},
			}));
			var stdin = proc.StandardInput.BaseStream;
			stdin.Write(Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n"));
			stdin.Write(body);
			stdin.Flush();

			// читаем stdout, пока не придёт весь первый кадр (заголовок + тело длиной Content-Length), не дольше минуты
			var received = new MemoryStream();
			var stdout = proc.StandardOutput.BaseStream;
			var chunk = new byte[4096];
			var deadline = DateTime.UtcNow.AddSeconds(60);
			string text = "";
			while (DateTime.UtcNow < deadline)
			{
				var read = stdout.ReadAsync(chunk, 0, chunk.Length);
				var left = deadline - DateTime.UtcNow;
				if (left <= TimeSpan.Zero || !read.Wait(left) || read.Result <= 0)
					break;
				received.Write(chunk, 0, read.Result);
				text = Encoding.UTF8.GetString(received.ToArray());
				var m = Regex.Match(text, @"Content-Length: (\d+)\r\n\r\n");
				if (m.Success && Encoding.UTF8.GetByteCount(text) - Encoding.UTF8.GetByteCount(text.Substring(0, m.Index + m.Length)) >= int.Parse(m.Groups[1].Value))
					break;
			}

			var head = text.Length > 200 ? text.Substring(0, 200) : text;
			StringAssert.StartsWith(text, "Content-Length:", $"stdout must start with a JSON-RPC frame; got: {head}\n--- stderr ---\n{stderr}");
			StringAssert.Contains(text, "\"protocolVersion\":2", "initialize response");
		}
		finally
		{
			try { proc.Kill(entireProcessTree: true); } catch { /* уже вышел */ }
		}
	}
}
