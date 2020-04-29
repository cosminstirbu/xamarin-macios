﻿using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.DotNet.XHarness.iOS.Shared;
using Microsoft.DotNet.XHarness.iOS.Shared.Hardware;
using Microsoft.DotNet.XHarness.iOS.Shared.Listeners;
using Microsoft.DotNet.XHarness.iOS.Shared.Logging;
using Microsoft.DotNet.XHarness.iOS.Shared.Tasks;
using Microsoft.DotNet.XHarness.iOS.Shared.Utilities;

namespace Xharness.TestTasks {
	public class RunDevice {
		readonly IRunXITask<IHardwareDevice> testTask;
		readonly IHardwareDeviceLoader devices;
		readonly IResultParser resultParser = new XmlResultParser ();
		readonly IResourceManager resourceManager;
		readonly ILog mainLog;
		readonly ILog deviceLoadLog;
		readonly bool uninstallTestApp;
		readonly bool cleanSuccessfulTestRuns;
		readonly bool generateXmlFailures;
		readonly bool inCI;
		readonly string defaultLogDirectory;
		readonly XmlResultJargon xmlResultJargon;

		public AppInstallMonitorLog InstallLog { get; private set; }

		public RunDevice (IRunXITask<IHardwareDevice> testTask,
					  	  IHardwareDeviceLoader devices,
						  IResourceManager resourceManager,
						  ILog mainLog,
						  ILog deviceLoadLog,
						  string defaultLogDirectory,
						  bool uninstallTestApp,
						  bool cleanSuccessfulTestRuns,
						  bool generateXmlFailures,
						  bool inCI,
						  XmlResultJargon xmlResultJargon)
		{
			this.testTask = testTask ?? throw new ArgumentNullException (nameof (testTask));
			this.devices = devices ?? throw new ArgumentNullException (nameof (devices));
			this.resourceManager = resourceManager ?? throw new ArgumentNullException (nameof (resourceManager));
			this.mainLog = mainLog ?? throw new ArgumentNullException (nameof (mainLog));
			this.deviceLoadLog = deviceLoadLog ?? throw new ArgumentNullException (nameof (deviceLoadLog));
			this.uninstallTestApp = uninstallTestApp;
			this.cleanSuccessfulTestRuns = cleanSuccessfulTestRuns;
			this.generateXmlFailures = generateXmlFailures;
			this.inCI = inCI;
			this.defaultLogDirectory = defaultLogDirectory ?? throw new ArgumentNullException (nameof (defaultLogDirectory)); // default should not be null
			this.xmlResultJargon = xmlResultJargon;

			switch (testTask.BuildTask.Platform) {
			case TestPlatform.iOS:
			case TestPlatform.iOS_Unified:
			case TestPlatform.iOS_Unified32:
			case TestPlatform.iOS_Unified64:
				testTask.AppRunnerTarget = TestTarget.Device_iOS;
				break;
			case TestPlatform.iOS_TodayExtension64:
				testTask.AppRunnerTarget = TestTarget.Device_iOS;
				break;
			case TestPlatform.tvOS:
				testTask.AppRunnerTarget = TestTarget.Device_tvOS;
				break;
			case TestPlatform.watchOS:
			case TestPlatform.watchOS_32:
			case TestPlatform.watchOS_64_32:
				testTask.AppRunnerTarget = TestTarget.Device_watchOS;
				break;
			}
		}

		public async Task RunTestAsync ()
		{
			mainLog.WriteLine ("Running '{0}' on device (candidates: '{1}')", testTask.ProjectFile, string.Join ("', '", testTask.Candidates.Select ((v) => v.Name).ToArray ()));

			var uninstall_log = testTask.Logs.Create ($"uninstall-{Helpers.Timestamp}.log", "Uninstall log");
			using (var device_resource = await testTask.NotifyBlockingWaitAsync (resourceManager.GetDeviceResources (testTask.Candidates).AcquireAnyConcurrentAsync ())) {
				try {
					// Set the device we acquired.
					testTask.Device = testTask.Candidates.First ((d) => d.UDID == device_resource.Resource.Name);
					if (testTask.Device.DevicePlatform == DevicePlatform.watchOS)
						testTask.CompanionDevice = await devices.FindCompanionDevice (deviceLoadLog, testTask.Device);
					mainLog.WriteLine ("Acquired device '{0}' for '{1}'", testTask.Device.Name, testTask.ProjectFile);

					testTask.Runner = new AppRunner (testTask.ProcessManager,
						new AppBundleInformationParser (),
						new SimulatorLoaderFactory (testTask.ProcessManager),
						new SimpleListenerFactory (),
						new DeviceLoaderFactory (testTask.ProcessManager),
						new CrashSnapshotReporterFactory (testTask.ProcessManager),
						new CaptureLogFactory (),
						new DeviceLogCapturerFactory (testTask.ProcessManager),
						new TestReporterFactory (testTask.ProcessManager),
						testTask.AppRunnerTarget,
						testTask.Harness,
						projectFilePath: testTask.ProjectFile,
						mainLog: uninstall_log,
						logs: new Logs (testTask.LogDirectory ?? defaultLogDirectory),
						buildConfiguration: testTask.ProjectConfiguration,
						deviceName: testTask.Device.Name,
						companionDeviceName: testTask.CompanionDevice?.Name,
						timeoutMultiplier: testTask.TimeoutMultiplier,
						variation: testTask.Variation,
						buildTask: testTask.BuildTask);

					// Sometimes devices can't upgrade (depending on what has changed), so make sure to uninstall any existing apps first.
					if (uninstallTestApp) {
						testTask.Runner.MainLog = uninstall_log;
						var uninstall_result = await testTask.Runner.UninstallAsync ();
						if (!uninstall_result.Succeeded)
							mainLog.WriteLine ($"Pre-run uninstall failed, exit code: {uninstall_result.ExitCode} (this hopefully won't affect the test result)");
					} else {
						uninstall_log.WriteLine ($"Pre-run uninstall skipped.");
					}

					if (!testTask.Failed) {
						// Install the app
						InstallLog = new AppInstallMonitorLog (testTask.Logs.Create ($"install-{Helpers.Timestamp}.log", "Install log"));
						try {
							testTask.Runner.MainLog = this.InstallLog;
							var install_result = await testTask.Runner.InstallAsync (InstallLog.CancellationToken);
							if (!install_result.Succeeded) {
								testTask.FailureMessage = $"Install failed, exit code: {install_result.ExitCode}.";
								testTask.ExecutionResult = TestExecutingResult.Failed;
								if (generateXmlFailures)
									resultParser.GenerateFailure (
										testTask.Logs,
										"install",
										testTask.Runner.AppInformation.AppName,
										testTask.Variation,
										$"AppInstallation on {testTask.Device.Name}",
										$"Install failed on {testTask.Device.Name}, exit code: {install_result.ExitCode}",
										InstallLog.FullPath, xmlResultJargon);
							}
						} finally {
							InstallLog.Dispose ();
							InstallLog = null;
						}
					}

					if (!testTask.Failed) {
						// Run the app
						testTask.Runner.MainLog = testTask.Logs.Create ($"run-{testTask.Device.UDID}-{Helpers.Timestamp}.log", "Run log");
						await testTask.Runner.RunAsync ();

						if (!string.IsNullOrEmpty (testTask.Runner.FailureMessage))
							testTask.FailureMessage = testTask.Runner.FailureMessage;
						else if (testTask.Runner.Result != TestExecutingResult.Succeeded)
							testTask.FailureMessage = testTask.GuessFailureReason (testTask.Runner.MainLog);

						if (testTask.Runner.Result == TestExecutingResult.Succeeded && testTask.Platform == TestPlatform.iOS_TodayExtension64) {
							// For the today extension, the main app is just a single test.
							// This is because running the today extension will not wake up the device,
							// nor will it close & reopen the today app (but launching the main app
							// will do both of these things, preparing the device for launching the today extension).

							AppRunner todayRunner = new AppRunner (testTask.ProcessManager,
								new AppBundleInformationParser (),
								new SimulatorLoaderFactory (testTask.ProcessManager),
								new SimpleListenerFactory (),
								new DeviceLoaderFactory (testTask.ProcessManager),
								new CrashSnapshotReporterFactory (testTask.ProcessManager),
								new CaptureLogFactory (),
								new DeviceLogCapturerFactory (testTask.ProcessManager),
								new TestReporterFactory (testTask.ProcessManager),
								testTask.AppRunnerTarget,
								testTask.Harness,
								projectFilePath: testTask.ProjectFile,
								mainLog: testTask.Logs.Create ($"extension-run-{testTask.Device.UDID}-{Helpers.Timestamp}.log", "Extension run log"),
								logs: new Logs (testTask.LogDirectory ?? defaultLogDirectory),
								buildConfiguration: testTask.ProjectConfiguration,
								deviceName: testTask.Device.Name,
								companionDeviceName: testTask.CompanionDevice?.Name,
								timeoutMultiplier: testTask.TimeoutMultiplier,
								variation: testTask.Variation,
								buildTask: testTask.BuildTask);

							testTask.AdditionalRunner = todayRunner;
							await todayRunner.RunAsync ();
							foreach (var log in todayRunner.Logs.Where ((v) => !v.Description.StartsWith ("Extension ", StringComparison.Ordinal)))
								log.Description = "Extension " + log.Description [0].ToString ().ToLower () + log.Description.Substring (1);
							testTask.ExecutionResult = todayRunner.Result;

							if (!string.IsNullOrEmpty (todayRunner.FailureMessage))
								testTask.FailureMessage = todayRunner.FailureMessage;
						} else {
							testTask.ExecutionResult = testTask.Runner.Result;
						}
					}
				} finally {
					// Uninstall again, so that we don't leave junk behind and fill up the device.
					if (uninstallTestApp) {
						testTask.Runner.MainLog = uninstall_log;
						var uninstall_result = await testTask.Runner.UninstallAsync ();
						if (!uninstall_result.Succeeded)
							mainLog.WriteLine ($"Post-run uninstall failed, exit code: {uninstall_result.ExitCode} (this won't affect the test result)");
					} else {
						uninstall_log.WriteLine ($"Post-run uninstall skipped.");
					}

					// Also clean up after us locally.
					if (inCI || cleanSuccessfulTestRuns && testTask.Succeeded)
						await testTask.BuildTask.CleanAsync ();
				}
			}
		}
	}
}