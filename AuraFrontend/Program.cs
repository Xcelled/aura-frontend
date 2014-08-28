﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Microsoft.Build.Execution;
using Microsoft.Build.Framework;
using Microsoft.Build.Logging;

namespace AuraFrontend
{
	class Program
	{
		private static readonly string GitClonePath = "https://github.com/aura-project/aura.git";

		private static readonly string ExeDir =
			Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);

		private static readonly string AuraDir = Path.Combine(ExeDir, "aura");
		private static readonly string SlnPath = Path.Combine(AuraDir, "Aura.sln");
		private static readonly string MainSqlPath = Path.Combine(AuraDir, "sql/main.sql");
		private static readonly string StartServersPath = Path.Combine(AuraDir, "start-server.bat");

		private static readonly string UniDir = Path.Combine(ExeDir, "uniserver");
		private static readonly string MySqlDir = Path.Combine(UniDir, "core/mysql/bin");
		private static readonly string MySqlDPath = Path.Combine(MySqlDir, "mysqld_z.exe");
		private static readonly string MySqlPath = Path.Combine(MySqlDir, "mysql.exe");
		private static readonly string MySqlArgs = "--user=root";

		private static readonly string MainRunPath = Path.Combine(ExeDir, ".mainrun");

		static void Main(string[] args)
		{
			KillMysql();

			var ports = new SortedDictionary<int, Tuple<string, bool>>
			{
				{ 11000, Tuple.Create("Login", true) },
				{ 11020, Tuple.Create("Channel", true) },
				{ 8002, Tuple.Create("Messenger", false) },
				{ 10999, Tuple.Create("Web API", false) },
				{ 80, Tuple.Create("HTTP/Hotkeys", false) },
				{ 3306, Tuple.Create("MySQL", true) },
			};

			var portTester = new PortTester(ports);

			if (!portTester.Test())
			{
				PrintError("Port check failed, startup aborted");
				Exit(true);
			}

			var recompileRequired = new UpdateSource(AuraDir, GitClonePath).Update();

			if (recompileRequired)
			{
				var compiler = new AuraCompiler(SlnPath, false);

				if (!compiler.Build())
				{
					PrintError("Recompilation failed due to one or more errors, startup aborted");
					Exit(true);
				}
			}

			using (var mysql = new MySqlServer(MySqlDPath, MySqlDir, MySqlArgs, MySqlPath))
			using (var servers = new AuraServers(AuraDir, StartServersPath))
			{
				mysql.Start();

				if (!File.Exists(MainRunPath))
				{
					if (mysql.RunMainSql(MainSqlPath))
					{
						File.WriteAllText(MainRunPath, "");
					}
					else
					{
						PrintError("Main.sql could not be applied. Startup will be terminated.");
						Exit(true);
					}
				}

				if (!servers.Start())
				{
					PrintError("Aura servers could not be started.");
					Exit(true);
				}
				Console.WriteLine();

				Console.WriteLine("Aura is now running.");
				Console.WriteLine("Once you have exited all Aura servers, return here and press any key to shut down MySql");

				Exit(true);
			}
		}

		static void Exit(bool wait)
		{
			if (wait)
			{
				while (Console.KeyAvailable)
					Console.ReadKey();
				
				Console.WriteLine();
				Console.WriteLine();

				Console.WriteLine("Press any key to exit . . .");
				Console.ReadKey(true);
			}

			Environment.Exit(0);
		}

		static void KillMysql()
		{
			var mysqls = Process.GetProcessesByName("mysqld_z");

			if (mysqls.Length != 0)
			{
				using (var _ = new ChangingOutput("Termininating existing MySql servers . . ."))
				{
					_.FinishLine();

					var success = true;

					foreach (var p in mysqls)
					{
						using (var t = new ChangingOutput("Terminating process {0} . . .", p.Id))
						{
							p.Kill();
							var killed = p.WaitForExit(10*1000);
							t.PrintResult(killed);

							if (!killed)
								success = false;
						}
					}

					_.PrintResult(success);
				}
			}
		}

		static void PrintWarning(string format, params object[] args)
		{
			var c = Console.ForegroundColor;
			Console.ForegroundColor = ConsoleColor.Yellow;
			Console.Write("WARNING: ");
			Console.WriteLine(format, args);
			Console.ForegroundColor = c;
		}

		static void PrintError(string format, params object[] args)
		{
			var c = Console.ForegroundColor;
			Console.ForegroundColor = ConsoleColor.Red;
			Console.Write("ERROR: ");
			Console.WriteLine(format, args);
			Console.ForegroundColor = c;
		}
	}
}
