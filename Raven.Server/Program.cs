using System;
using System.Configuration.Install;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Principal;
using System.ServiceProcess;
using log4net.Appender;
using log4net.Config;
using log4net.Filter;
using log4net.Layout;
using Raven.Database;

namespace Raven.Server
{
	internal class Program
	{
		private static void Main(string[] args)
		{
			if (Environment.UserInteractive)
			{
				switch (GetArgument(args))
				{
					case "install":
						AdminRequired(InstallAndStart, "/install");
						break;
					case "uninstall":
						AdminRequired(EnsureStoppedAndUninstall, "/uninstall");
						break;
					case "start":
						AdminRequired(StartService, "/start");
						break;
					case "stop":
						AdminRequired(StopService, "/stop");
						break;
					case "debug":
						RunInDebugMode(createDefaultDatabase: true, anonymousUserAccessMode: null);
						break;
#if DEBUG
					case "test":
						var dataDirectory = new RavenConfiguration().DataDirectory;
						if (Directory.Exists(dataDirectory))
							Directory.Delete(dataDirectory, true);

						RunInDebugMode(createDefaultDatabase: false, anonymousUserAccessMode: AnonymousUserAccessMode.All);
						break;
#endif
					default:
						PrintUsage();
						break;
				}
			}
			else
			{
				ServiceBase.Run(new RavenService());
			}
		}

		private static void AdminRequired(Action actionThatMayRequiresAdminPrivileges, string cmdLine)
		{
			var principal = new WindowsPrincipal(WindowsIdentity.GetCurrent());
			if (principal.IsInRole(WindowsBuiltInRole.Administrator) == false)
			{
				if (RunAgainAsAdmin(cmdLine))
					return;
			}
			actionThatMayRequiresAdminPrivileges();
		}

		private static bool RunAgainAsAdmin(string cmdLine)
		{
			try
			{
				Process.Start(new ProcessStartInfo
				{
					Arguments = cmdLine,
					FileName = Assembly.GetExecutingAssembly().Location,
					Verb = "runas",
				}).WaitForExit();
				return true;
			}
			catch (Exception)
			{
				return false;
			}
		}

		private static string GetArgument(string[] args)
		{
			if (args.Length == 0)
				return "debug";
			if (args.Length > 1 || args[0].StartsWith("/") == false)
				return "help";
			return args[0].Substring(1);
		}

		private static void RunInDebugMode(bool createDefaultDatabase, AnonymousUserAccessMode? anonymousUserAccessMode)
		{
			var consoleAppender = new ConsoleAppender
			{
				Layout = new PatternLayout(PatternLayout.DefaultConversionPattern),
			};
			consoleAppender.AddFilter(new LoggerMatchFilter
			{
				AcceptOnMatch = true,
				LoggerToMatch = typeof (HttpServer).FullName
			});
			BasicConfigurator.Configure(consoleAppender);
            var ravenConfiguration = new RavenConfiguration
            {
                ShouldCreateDefaultsWhenBuildingNewDatabaseFromScratch = createDefaultDatabase,
            };
            RavenDbServer.EnsureCanListenToWhenInNonAdminContext(ravenConfiguration.Port);
			if (anonymousUserAccessMode.HasValue)
				ravenConfiguration.AnonymousUserAccessMode = anonymousUserAccessMode.Value;
			using (new RavenDbServer(ravenConfiguration))
			{
				Console.WriteLine("Raven is ready to process requests.");
				Console.WriteLine("Press any key to stop the server");
				Console.ReadLine();
			}
		}

		private static void PrintUsage()
		{
			Console.WriteLine(
				@"
Raven DB
Document Database for the .Net Platform
----------------------------------------
Copyright (C) 2010 - Hibernating Rhinos
----------------------------------------
Command line ptions:
    raven             - with no args, starts Raven in local server mode
    raven /install    - installs and starts the Raven service
    raven /unisntall  - stops and uninstalls the Raven service

Enjoy...
");
		}

		private static void EnsureStoppedAndUninstall()
		{
			if (ServiceIsInstalled() == false)
			{
				Console.WriteLine("Service is not installed");
			}
			else
			{
				var stopController = new ServiceController(ProjectInstaller.SERVICE_NAME);

				if (stopController.Status == ServiceControllerStatus.Running)
					stopController.Stop();

				ManagedInstallerClass.InstallHelper(new[] {"/u", Assembly.GetExecutingAssembly().Location});
			}
		}

		private static void StopService()
		{
			var stopController = new ServiceController(ProjectInstaller.SERVICE_NAME);

			if (stopController.Status == ServiceControllerStatus.Running)
				stopController.Stop();
		}


		private static void StartService()
		{
			var stopController = new ServiceController(ProjectInstaller.SERVICE_NAME);

			if (stopController.Status != ServiceControllerStatus.Running)
				stopController.Start();
		}

		private static void InstallAndStart()
		{
			if (ServiceIsInstalled())
			{
				Console.WriteLine("Service is already installed");
			}
			else
			{
				ManagedInstallerClass.InstallHelper(new[] {Assembly.GetExecutingAssembly().Location});
				var startController = new ServiceController(ProjectInstaller.SERVICE_NAME);
				startController.Start();
			}
		}

		private static bool ServiceIsInstalled()
		{
			return (ServiceController.GetServices().Count(s => s.ServiceName == ProjectInstaller.SERVICE_NAME) > 0);
		}
	}
}