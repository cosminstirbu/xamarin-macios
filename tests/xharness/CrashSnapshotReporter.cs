﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xharness.Execution;
using Xharness.Execution.Mlaunch;
using Xharness.Logging;

namespace Xharness
{
	public interface ICrashSnapshotReporterFactory {
		ICrashSnapshotReporter Create (ILog log, ILogs logs, bool isDevice, string deviceName);
	}

	public class CrashSnapshotReporterFactory : ICrashSnapshotReporterFactory {
		readonly IProcessManager processManager;
		readonly string xcodeRoot;
		readonly string mlaunchPath;

		public CrashSnapshotReporterFactory (IProcessManager processManager, string xcodeRoot, string mlaunchPath)
		{
			this.processManager = processManager ?? throw new ArgumentNullException (nameof (processManager));
			this.xcodeRoot = xcodeRoot ?? throw new ArgumentNullException (nameof (xcodeRoot));
			this.mlaunchPath = mlaunchPath ?? throw new ArgumentNullException (nameof (mlaunchPath));
		}

		public ICrashSnapshotReporter Create (ILog log, ILogs logs, bool isDevice, string deviceName) =>
			new CrashSnapshotReporter (processManager, log, logs, xcodeRoot, mlaunchPath, isDevice, deviceName);
	}

	public interface ICrashSnapshotReporter {
		Task EndCaptureAsync (TimeSpan timeout);
		Task StartCaptureAsync ();
	}

	public class CrashSnapshotReporter : ICrashSnapshotReporter {
		readonly IProcessManager processManager;
		readonly ILog log;
		readonly ILogs logs;
		readonly string xcodeRoot;
		readonly string mlaunchPath;
		readonly bool isDevice;
		readonly string deviceName;
		readonly Func<string> tempFileProvider;
		readonly string symbolicateCrashPath;

		HashSet<string> initialCrashes;

		public CrashSnapshotReporter (IProcessManager processManager,
									  ILog log,
								      ILogs logs,
									  string xcodeRoot,
								      string mlaunchPath,
								      bool isDevice,
								      string deviceName,
									  Func<string> tempFileProvider = null)
		{
			this.processManager = processManager ?? throw new ArgumentNullException (nameof (processManager));
			this.log = log ?? throw new ArgumentNullException (nameof (log));
			this.logs = logs ?? throw new ArgumentNullException (nameof (logs));
			this.xcodeRoot = xcodeRoot ?? throw new ArgumentNullException (nameof (xcodeRoot));
			this.mlaunchPath = mlaunchPath ?? throw new ArgumentNullException (nameof (mlaunchPath));
			this.isDevice = isDevice;
			this.deviceName = deviceName;
			this.tempFileProvider = tempFileProvider ?? Path.GetTempFileName;
			
			symbolicateCrashPath = Path.Combine (xcodeRoot, "Contents", "SharedFrameworks", "DTDeviceKitBase.framework", "Versions", "A", "Resources", "symbolicatecrash");
			if (!File.Exists (symbolicateCrashPath))
				symbolicateCrashPath = Path.Combine (xcodeRoot, "Contents", "SharedFrameworks", "DVTFoundation.framework", "Versions", "A", "Resources", "symbolicatecrash");
			if (!File.Exists (symbolicateCrashPath))
				symbolicateCrashPath = null;
		}

		public async Task StartCaptureAsync ()
		{
			initialCrashes = await CreateCrashReportsSnapshotAsync ();
		}

		public async Task EndCaptureAsync (TimeSpan timeout)
		{
			// Check for crash reports
			var stopwatch = Stopwatch.StartNew ();

			do {
				var newCrashes = await CreateCrashReportsSnapshotAsync ();
				newCrashes.ExceptWith (initialCrashes);

				if (newCrashes.Count == 0) {
					if (stopwatch.Elapsed.TotalSeconds > timeout.TotalSeconds) {
						break;
					} else {
						log.WriteLine (
							"No crash reports, waiting a second to see if the crash report service just didn't complete in time ({0})",
							(int) (timeout.TotalSeconds - stopwatch.Elapsed.TotalSeconds));
						
						Thread.Sleep (TimeSpan.FromSeconds (1));
					}

					continue;
				}

				log.WriteLine ("Found {0} new crash report(s)", newCrashes.Count);

				List<ILogFile> crashReports;
				if (!isDevice) {
					crashReports = new List<ILogFile> (newCrashes.Count);
					foreach (var path in newCrashes) {
						logs.AddFile (path, $"Crash report: {Path.GetFileName (path)}");
					}
				} else {
					// Download crash reports from the device. We put them in the project directory so that they're automatically deleted on wrench
					// (if we put them in /tmp, they'd never be deleted).
					crashReports = new List<ILogFile> ();
					foreach (var crash in newCrashes) {
						var name = Path.GetFileName (crash);
						var crashReportFile = logs.Create (name, $"Crash report: {name}", timestamp: false);
						var args = new MlaunchArguments (
							new DownloadCrashReportArgument (crash),
							new DownloadCrashReportToArgument (crashReportFile.Path),
							new SdkRootArgument (xcodeRoot));

						if (!string.IsNullOrEmpty (deviceName)) {
							args.Add (new DeviceNameArgument(deviceName));
						}

						var result = await processManager.ExecuteCommandAsync (mlaunchPath, args, log, TimeSpan.FromMinutes (1));

						if (result.Succeeded) {
							log.WriteLine ("Downloaded crash report {0} to {1}", crash, crashReportFile.Path);
							crashReportFile = await GetSymbolicateCrashReportAsync (crashReportFile);
							crashReports.Add (crashReportFile);
						} else {
							log.WriteLine ("Could not download crash report {0}", crash);
						}
					}
				}

				foreach (var cp in crashReports) {
					WrenchLog.WriteLine ("AddFile: {0}", cp.Path);
					log.WriteLine ("    {0}", cp.Path);
				}

				break;

			} while (true);
		}

		async Task<ILogFile> GetSymbolicateCrashReportAsync (ILogFile report)
		{
			if (symbolicateCrashPath == null) {
				log.WriteLine ("Can't symbolicate {0} because the symbolicatecrash script {1} does not exist", report.Path, symbolicateCrashPath);
				return report;
			}

			var name = Path.GetFileName (report.Path);
			var symbolicated = logs.Create (Path.ChangeExtension (name, ".symbolicated.log"), $"Symbolicated crash report: {name}", timestamp: false);
			var environment = new Dictionary<string, string> { { "DEVELOPER_DIR", Path.Combine (xcodeRoot, "Contents", "Developer") } };
			var result = await processManager.ExecuteCommandAsync (symbolicateCrashPath, new [] { report.Path }, symbolicated, TimeSpan.FromMinutes (1), environment);
			if (result.Succeeded) {
				log.WriteLine ("Symbolicated {0} successfully.", report.Path);
				return symbolicated;
			} else {
				log.WriteLine ("Failed to symbolicate {0}.", report.Path);
				return report;
			}
		}

		async Task<HashSet<string>> CreateCrashReportsSnapshotAsync ()
		{
			var crashes = new HashSet<string> ();

			if (!isDevice) {
				var dir = Path.Combine (Environment.GetEnvironmentVariable ("HOME"), "Library", "Logs", "DiagnosticReports");
				if (Directory.Exists (dir))
					crashes.UnionWith (Directory.EnumerateFiles (dir));
			} else {
				var tempFile = tempFileProvider ();
				try {
					var args = new MlaunchArguments (
						new ListCrashReportsArgument (tempFile),
						new SdkRootArgument (xcodeRoot));

					if (!string.IsNullOrEmpty (deviceName)) {
						args.Add (new DeviceNameArgument(deviceName));
					}

					var result = await processManager.ExecuteCommandAsync (mlaunchPath, args, log, TimeSpan.FromMinutes (1));
					if (result.Succeeded)
						crashes.UnionWith (File.ReadAllLines (tempFile));
				} finally {
					File.Delete (tempFile);
				}
			}

			return crashes;
		}
	}
}