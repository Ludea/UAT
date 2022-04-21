// Copyright Epic Games, Inc. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.IO;
using AutomationTool;
using UnrealBuildTool;
using System.Threading;
using System.Text.RegularExpressions;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Security.Cryptography;

namespace Gauntlet
{
	/// <summary>
	/// Implementation of a Gauntlet TestNode that is capable of executing tests on an Unreal "session" where multiple
	/// Unreal instances may be involved. This class leans on UnrealSession to do the work of spinning up, monitoring, and
	/// shutting down instances. Those operations plus basic validation of Unreal's functionality are used to provide the
	/// required ITestNode interfaces
	/// </summary>
	/// <typeparam name="TConfigClass"></typeparam>
	public abstract class UnrealTestNode<TConfigClass> : BaseTest, IDisposable
		where TConfigClass : UnrealTestConfiguration, new()
	{
		[Flags]
		public enum BehaviorFlags
		{
			None = 0,
			PromoteErrors = 1,              // Promote errors from Unreal instances to regular test errors. (By default only fatal errors are errors)
			PromoteWarnings = 2,            // Promote warnings from Unreal instances to regular test warnings.	(By default only ensures are warnings)
		}

		/// <summary>
		/// Returns an identifier for this test
		/// </summary>
		public override string Name { get { return this.GetType().FullName; } }

		/// <summary>
		/// This class will log its own warnings and errors as part of its summary
		/// </summary>
		public override bool LogWarningsAndErrorsAfterSummary { get; protected set; } = false;

		/// <summary>
		/// How long this test should run for, set during LaunchTest based on results of GetConfiguration
		/// </summary>
		public override float MaxDuration { get; protected set; }


		/// Behavior flags for this test
		/// </summary>
		public BehaviorFlags Flags { get; protected set; }
		/// <summary>
		/// Priority of this test
		/// </summary>
		public override TestPriority Priority { get { return GetPriority(); } }

		/// <summary>
		/// Returns a list of all log channels the heartbeat tick should look for.
		/// </summary>
		public virtual IEnumerable<string> GetHeartbeatLogCategories()
		{
			return Enumerable.Empty<string>();
		}

		/// <summary>
		/// Returns Warnings found during tests. By default only ensures and are considered
		/// </summary>
		public override IEnumerable<string> GetWarnings()
		{
			IEnumerable<string> WarningList = Events.Where(E => E.IsWarning).Select(E => E.Message);
			
			if (RoleResults != null)
			{
				WarningList = WarningList.Union(RoleResults.SelectMany(R => R.Events.Where(E => E.Severity == EventSeverity.Warning)).Select(E => E.Summary));
			}

			return WarningList.ToArray();
		}

		/// <summary>
		/// Returns Errors found during tests. By default fatal and error severities are considered.
		/// returning lists of event summaries
		/// </summary>
		public override IEnumerable<string> GetErrors()
		{
			IEnumerable<string> ErrorList = Events.Where(E => E.IsError).Select(E => E.Message);
			
			if (RoleResults != null)
			{
				ErrorList = ErrorList.Union(RoleResults.SelectMany(R => R.Events.Where(E => E.Severity == EventSeverity.Error || E.Severity == EventSeverity.Fatal)).Select(E => E.Summary));
			}

			return ErrorList.ToArray();			
		}

		/// <summary>
		/// Returns Errors found during tests. Including Abnormal Exit reasons
		/// </summary>
		public virtual IEnumerable<string> GetErrorsAndAbnornalExits()
		{
			IEnumerable<string> Errors = GetErrors();
			if (RoleResults == null)
			{
				return Errors;
			}

			return Errors.Union(RoleResults.Where(R => R.ProcessResult != UnrealProcessResult.ExitOk).Select(
				R => string.Format("Abnormal Exit: Reason={0}, ExitCode={1}, Log={2}", R.Summary, R.ExitCode, Path.GetFileName(R.Artifacts.LogPath))
			));
		}

		/// <summary>
		/// Returns the test URL Link
		/// </summary>
		public virtual string GetURLLink()
		{
			return "";
		}

		/// <summary>
		/// Report an error
		/// </summary>
		/// <param name="Message"></param>
		public virtual void ReportError(string Message, params object[] Args)
		{
			Message = string.Format(Message, Args);
			Events.Add(new UnrealAutomationEvent(EventType.Error, Message));
			if (!LogWarningsAndErrorsAfterSummary) { Log.Error(Message); }
			if (GetTestStatus() == TestStatus.Complete && GetTestResult() == TestResult.Passed)
			{
				SetUnrealTestResult(TestResult.Failed);
			}
		}

		/// <summary>
		/// Report a warning
		/// </summary>
		/// <param name="Message"></param>
		public virtual void ReportWarning(string Message, params object[] Args)
		{
			Message = string.Format(Message, Args);
			if (!LogWarningsAndErrorsAfterSummary) { Events.Add(new UnrealAutomationEvent(EventType.Warning, Message)); }
			Log.Warning(Message);
		}

		// Begin UnrealTestNode properties and members

		/// <summary>
		/// Our context that holds environment wide info about the required conditions for the test
		/// </summary>
		public UnrealTestContext Context { get; private set; }

		/// <summary>
		/// When the test is running holds all running Unreal processes (clients, servers etc).
		/// </summary>
		public UnrealSessionInstance TestInstance { get; private set; }

		/// <summary>
		/// Describes the post-test results for a role.
		/// </summary>
		public class UnrealRoleResult
		{
			/// <summary>
			/// High-level description of how the process ended
			/// </summary>
			public UnrealProcessResult ProcessResult;

			/// <summary>
			/// Exit code for the process. Unreal makes limited use of exit codes so in most cases
			/// this will be 0 / -1
			/// </summary>
			public int ExitCode;

			/// <summary>
			/// Human-readable of the process result. (E.g 'process encountered a fatal error')
			/// </summary>
			public string Summary;

			/// <summary>
			/// A summary of information such as entries, warnings, errors, ensures, etc etc extracted from the log
			/// </summary>
			public UnrealLog LogSummary;

			/// <summary>
			/// Artifacts for this role. 
			/// </summary>
			public UnrealRoleArtifacts Artifacts;

			/// <summary>
			/// Events that occurred during the test pass. Asserts/Ensures/Errors/Warnings should all be in here
			/// </summary>
			public IEnumerable<UnrealTestEvent> Events;

			/// <summary>
			/// Constructor. All members are required
			/// </summary>
			public UnrealRoleResult(UnrealProcessResult InResult, int InExitCode, string InSummary, UnrealLog InLog, UnrealRoleArtifacts InArtifacts, IEnumerable<UnrealTestEvent> InEvents)
			{
				ProcessResult = InResult;
				ExitCode = InExitCode;
				Summary = InSummary;
				LogSummary = InLog;
				Artifacts = InArtifacts;
				Events = InEvents;
			}
		};

		/// <summary>
		/// After the test completes holds artifacts for each process (clients, servers etc).
		/// </summary>
		public IEnumerable<UnrealRoleResult> RoleResults { get; private set; }

		/// <summary>
		/// After the test completes holds artifacts for each process (clients, servers etc).
		/// </summary>
		public IEnumerable<UnrealRoleArtifacts> SessionArtifacts { get; private set; }

		/// <summary>
		/// Error and warning collection.
		/// </summary>
		protected List<UnrealAutomationEvent> Events { get; private set; } = new List<UnrealAutomationEvent>();

		/// <summary>
		/// Whether we submit to the dashboard
		/// </summary>
		public virtual bool ShouldSubmitDashboardResult { get { return CommandUtils.IsBuildMachine; } }

		/// <summary>
		/// Helper class that turns our wishes into reality
		/// </summary>
		protected UnrealSession UnrealApp;

		/// <summary>
		/// Used to track how much of our app log has been written out
		/// </summary>
		private int LastAppLogCount;

		/// <summary>
		/// Used to track how much of our editor log has been written out
		/// </summary>
		private int LastEditorLogCount;

		private int CurrentPass;

		private int NumPasses;

		static protected DateTime SessionStartTime = DateTime.MinValue;

		private int Retries = 0;
		private int MaxRetries = 3;

		/// <summary>
		/// Standard semantic versioning for tests. Should be overwritten within individual tests, and individual test maintainers
		/// are responsible for updating their own versions. See https://semver.org/ for more info on maintaining semantic versions.
		/// </summary>
		/// 
		protected Version TestVersion;

		/// <summary>
		/// Path to the directory that logs and other artifacts are copied to after the test run.
		/// </summary>
		protected string ArtifactPath { get; private set; }

		/// <summary>
		/// Our test result. May be set directly, or by overriding GetUnrealTestResult()
		/// </summary>
		private TestResult UnrealTestResult;

		protected TConfigClass CachedConfig = null;

		protected DateTime TimeOfFirstMissingProcess;

		protected int TimeToWaitForProcesses { get; set; }
		
		protected DateTime LastHeartbeatTime = DateTime.MinValue;
		protected DateTime LastActiveHeartbeatTime = DateTime.MinValue;

		// End  UnrealTestNode properties and members 

		// artifact paths that have been used in this run
		static protected HashSet<string> ReservedArtifcactPaths = new HashSet<string>();

		/// <summary>
		/// Help doc-style list of parameters supported by this test. List can be divided into test-specific and general arguments.
		/// </summary>
		public List<GauntletParamDescription> SupportedParameters = new List<GauntletParamDescription>();

		/// <summary>
		/// Optional list of provided commandlines to be displayed to users who want to look at test help docs.
		/// Key should be the commandline to use, value should be the description for that commandline.
		/// </summary>
		protected List<KeyValuePair<string, string>> SampleCommandlines = new List<KeyValuePair<string, string>>();

		public void AddSampleCommandline(string Commandline, string Description)
		{
			SampleCommandlines.Add(new KeyValuePair<string, string>(Commandline, Description));
		}

		/// <summary>
		/// Constructor. A context of the correct type is required
		/// </summary>
		/// <param name="InContext"></param>
		public UnrealTestNode(UnrealTestContext InContext)
		{
			Context = InContext;

			UnrealTestResult = TestResult.Invalid;
			TimeToWaitForProcesses = 5;
			LastAppLogCount = 0;
			LastEditorLogCount = 0;
			CurrentPass = 0;
			NumPasses = 0;
			TestVersion = new Version("1.0.0");
			ArtifactPath = string.Empty;
			PopulateCommandlineInfo();
			// We format warnings ourselves so don't show these
			LogWarningsAndErrorsAfterSummary = false;
		}

		 ~UnrealTestNode()
		{
			Dispose(false);
		}

		#region IDisposable Support
		private bool disposedValue = false; // To detect redundant calls

		protected virtual void Dispose(bool disposing)
		{
			if (!disposedValue)
			{
				if (disposing)
				{
					// TODO: dispose managed state (managed objects).
				}

				CleanupTest();

				disposedValue = true;
			}
		}

		// This code added to correctly implement the disposable pattern.
		public void Dispose()
		{
			// Do not change this code. Put cleanup code in Dispose(bool disposing) above.
			Dispose(true);
		}
		#endregion

		public override String ToString()
		{
			if (Context == null)
			{
				return Name;
			}

			return string.Format("{0} ({1})", Name, Context);
		}

		/// <summary>
		/// Sets the context that tests run under. Called once during creation
		/// </summary>
		/// <param name="InContext"></param>
		/// <returns></returns>
		public override void SetContext(ITestContext InContext)
		{
			Context = InContext as UnrealTestContext;
		}


		/// <summary>
		/// Returns information about how to configure our Unreal processes. For the most part the majority
		/// of Unreal tests should only need to override this function
		/// </summary>
		/// <returns></returns>
		public virtual TConfigClass GetConfiguration()
		{
			if (CachedConfig == null)
			{
				CachedConfig = new TConfigClass();
				AutoParam.ApplyParamsAndDefaults(CachedConfig, this.Context.TestParams.AllArguments);
			}
			return CachedConfig;
		}

		/// <summary>
		/// Returns the cached version of our config. Avoids repeatedly calling GetConfiguration() on derived nodes
		/// </summary>
		/// <returns></returns>
		private TConfigClass GetCachedConfiguration()
		{
			if (CachedConfig == null)
			{
				return GetConfiguration();
			}

			return CachedConfig;
		}

		/// <summary>
		/// Returns a priority value for this test
		/// </summary>
		/// <returns></returns>
		protected TestPriority GetPriority()
		{
			IEnumerable<UnrealTargetPlatform> DesktopPlatforms = UnrealBuildTool.Utils.GetPlatformsInClass(UnrealPlatformClass.Desktop);

			UnrealTestRoleContext ClientContext = Context.GetRoleContext(UnrealTargetRole.Client);

			// because these device build need deployed we want them in flight asap
			IDeviceBuildSupport DeviceBuildSupport = Gauntlet.Utils.InterfaceHelpers.FindImplementations<IDeviceBuildSupport>().Where(D => D.CanSupportPlatform(ClientContext.Platform)).FirstOrDefault(); ;
			if (DeviceBuildSupport != null && DeviceBuildSupport.NeedBuildDeployed())
			{
				return TestPriority.High;
			}

			return TestPriority.Normal;
		}

		protected virtual IEnumerable<UnrealSessionInstance.RoleInstance> FindAnyMissingRoles()
		{
			return TestInstance.RunningRoles.Where(R => R.AppInstance.HasExited);
		}

		/// <summary>
		/// Checks whether the test is still running. The default implementation checks whether all of our processes
		/// are still alive.
		/// </summary>
		/// <returns></returns>
		public virtual bool IsTestRunning()
		{
			var MissingRoles = FindAnyMissingRoles().ToList();

			if (MissingRoles.Count == 0)
			{
				// nothing missing, keep going.
				return true;
			}
			
			// if all roles are gone, we're done
			if (MissingRoles.Count == TestInstance.RunningRoles.Count())
			{
				return false;
			}

			// This test only ends when all roles are gone
			if (GetCachedConfiguration().AllRolesExit)
			{
				return true;
			}

			if (TimeOfFirstMissingProcess == DateTime.MinValue)
			{
				TimeOfFirstMissingProcess = DateTime.Now;
				Log.Verbose("Role {0} exited. Waiting {1} seconds for others to exit", MissingRoles.First().ToString(), TimeToWaitForProcesses);
			}

			if ((DateTime.Now - TimeOfFirstMissingProcess).TotalSeconds < TimeToWaitForProcesses)
			{
				// give other processes time to exit normally
				return true;
			}

			Log.Info("Ending {0} due to exit of Role {1}. {2} processes still running", Name, MissingRoles.First().ToString(), TestInstance.RunningRoles.Count());

			// Done!
			return false;
		}

		protected bool PrepareUnrealApp()
		{
			// Get our configuration
			TConfigClass Config = GetCachedConfiguration();

			if (Config == null)
			{
				throw new AutomationException("Test {0} returned null config!", this);
			}

			if (UnrealApp != null)
			{
				throw new AutomationException("Node already has an UnrealApp, was PrepareUnrealSession called twice?");
			}

			// pass through any arguments such as -TestNameArg or -TestNameArg=Value
			var TestName = this.GetType().Name;
			var ShortName = TestName.Replace("Test", "");

			var PassThroughArgs = Context.TestParams.AllArguments
				.Where(A => A.StartsWith(TestName, System.StringComparison.OrdinalIgnoreCase) || A.StartsWith(ShortName, System.StringComparison.OrdinalIgnoreCase))
				.Select(A =>
				{
					A = "-" + A;

					var EqIndex = A.IndexOf("=");

					// no =? Just a -switch then
					if (EqIndex == -1)
					{
						return A;
					}

					var Cmd = A.Substring(0, EqIndex + 1);
					var Args = A.Substring(EqIndex + 1);

					// if no space in the args, just leave it
					if (Args.IndexOf(" ") == -1)
					{
						return A;
					}

					return string.Format("{0}\"{1}\"", Cmd, Args);
				});

			List<UnrealSessionRole> SessionRoles = new List<UnrealSessionRole>();

			// Go through each type of role that was required and create a session role
			foreach (var TypesToRoles in Config.RequiredRoles)
			{
				// get the actual context of what this role means.
				UnrealTestRoleContext RoleContext = Context.GetRoleContext(TypesToRoles.Key);

				foreach (UnrealTestRole TestRole in TypesToRoles.Value)
				{
					// If a config has overriden a platform then we can't use the context constraints from the commandline
					bool UseContextConstraint = TestRole.Type == UnrealTargetRole.Client && TestRole.PlatformOverride == null;

					// important, use the type from the ContextRolke because Server may have been mapped to EditorServer etc
					UnrealTargetPlatform SessionPlatform = TestRole.PlatformOverride ?? RoleContext.Platform;

					UnrealSessionRole SessionRole = new UnrealSessionRole(RoleContext.Type, SessionPlatform, RoleContext.Configuration, TestRole.CommandLine);
					SessionRole.InstallOnly = TestRole.InstallOnly;
					SessionRole.CommandLineParams = TestRole.CommandLineParams;
 					SessionRole.RoleModifier = TestRole.RoleType;
					SessionRole.Constraint = UseContextConstraint ? Context.Constraint : new UnrealDeviceTargetConstraint(SessionPlatform);
					Log.Verbose("Created SessionRole {0} from RoleContext {1} (RoleType={2})", SessionRole, RoleContext, TypesToRoles.Key);

					// TODO - this can all / mostly go into UnrealTestConfiguration.ApplyToConfig

					// Deal with command lines
					if (string.IsNullOrEmpty(TestRole.ExplicitClientCommandLine) == false)
					{
						SessionRole.CommandLine = TestRole.ExplicitClientCommandLine;
					}
					else
					{
						// start with anything from our context
						SessionRole.CommandLine += RoleContext.ExtraArgs;

						// did the test ask for anything?
						if (string.IsNullOrEmpty(TestRole.CommandLine) == false)
						{
							SessionRole.CommandLine += TestRole.CommandLine;
						}

						// add controllers
						SessionRole.CommandLineParams.Add("gauntlet", 
							TestRole.Controllers.Count > 0 ? string.Join(",", TestRole.Controllers) : null);				

						if (PassThroughArgs.Count() > 0)
						{
							SessionRole.CommandLine += " " + string.Join(" ", PassThroughArgs);
						}

						// add options
						SessionRole.Options = Config;
					}

					if (RoleContext.Skip)
					{
						SessionRole.RoleModifier = ERoleModifier.Null;
					}

					// copy over relevant settings from test role
                    SessionRole.FilesToCopy = TestRole.FilesToCopy;
					SessionRole.AdditionalArtifactDirectories = TestRole.AdditionalArtifactDirectories;
					SessionRole.ConfigureDevice = TestRole.ConfigureDevice;
					SessionRole.MapOverride = TestRole.MapOverride;

					SessionRoles.Add(SessionRole);
				}
			}

			UnrealApp = new UnrealSession(Context.BuildInfo, SessionRoles) { Sandbox = Context.Options.Sandbox };

			return true;
		}

		public override bool IsReadyToStart()
		{
			if (UnrealApp == null)
			{
				PrepareUnrealApp();
			}

			return UnrealApp.TryReserveDevices();
		}

		/// <summary>
		/// Generate an unique path for the test artifacts and reserve it
		/// </summary>
		/// <returns></returns>
		protected String ReserveArtifactPath()
		{
			// Either use the ArtifactName param or name of this test
			string TestFolder = string.IsNullOrEmpty(Context.Options.ArtifactName) ? this.ToString() : Context.Options.ArtifactName;

			if (string.IsNullOrEmpty(Context.Options.ArtifactPostfix) == false)
			{
				TestFolder += "_" + Context.Options.ArtifactPostfix;
			}

			TestFolder = TestFolder.Replace(" ", "_");
			TestFolder = TestFolder.Replace(":", "_");
			TestFolder = TestFolder.Replace("|", "_");
			TestFolder = TestFolder.Replace(",", "");

			ArtifactPath = Path.Combine(Context.Options.LogDir, TestFolder);

			// if doing multiple passes, put each in a subdir
			if (NumPasses > 1)
			{
				ArtifactPath = Path.Combine(ArtifactPath, string.Format("Pass_{0}_of_{1}", CurrentPass + 1, NumPasses));
			}

			if (Retries > 0)
			{
				ArtifactPath = Path.Combine(ArtifactPath, string.Format("Retry_{0}", Retries));
			}

			// When running with -parallel we could have several identical tests (same test, configurations) in flight so
			// we need unique artifact paths. We also don't overwrite dest directories from the build machine for the same
			// reason of multiple tests for a build. Really though these should use ArtifactPrefix to save to
			// SmokeTest_HighQuality etc
			int ArtifactNumericPostfix = 0;
			bool ArtifactPathIsTaken = false;

			do
			{
				string PotentialPath = ArtifactPath;

				if (ArtifactNumericPostfix > 0)
				{
					PotentialPath = string.Format("{0}_{1}", ArtifactPath, ArtifactNumericPostfix);
				}

				ArtifactPathIsTaken = ReservedArtifcactPaths.Contains(PotentialPath) || (CommandUtils.IsBuildMachine && Directory.Exists(PotentialPath));

				if (ArtifactPathIsTaken)
				{
					Log.Info("Directory already exists at {0}", PotentialPath);
					ArtifactNumericPostfix++;
				}
				else
				{
					ArtifactPath = PotentialPath;
				}

			} while (ArtifactPathIsTaken);

			ReservedArtifcactPaths.Add(ArtifactPath);

			return ArtifactPath;
		}

		/// <summary>
		/// Called by the test executor to start our test running. After this
		/// Test.Status should return InProgress or greater
		/// </summary>
		/// <returns></returns>
		public override bool StartTest(int Pass, int InNumPasses)
		{
			if (UnrealApp == null)
			{
				throw new AutomationException("Node already has a null UnrealApp, was PrepareUnrealSession or IsReadyToStart called?");
			}

			// ensure we reset things
			SessionArtifacts = Enumerable.Empty<UnrealRoleArtifacts>();
			RoleResults = Enumerable.Empty<UnrealRoleResult>();
			UnrealTestResult = TestResult.Invalid;
			CurrentPass = Pass;
			NumPasses = InNumPasses;
			LastAppLogCount = 0;
			LastEditorLogCount = 0;
			LastHeartbeatTime = DateTime.MinValue;
			LastActiveHeartbeatTime = DateTime.MinValue;			

			TConfigClass Config = GetCachedConfiguration();

			ReserveArtifactPath();

			// We need to create this directory at the start of the test rather than the end of the test - we are running into instances where multiple A/B tests
			// on the same build are seeing the directory as non-existent and thinking it is safe to write to.
			Log.Info("UnrealTestNode.StartTest Calling CreateDirectory for artifacts at {0}", ArtifactPath);
			Directory.CreateDirectory(ArtifactPath);

			// Launch the test
			TestInstance = UnrealApp.LaunchSession();
			// Add info from test context to device usage log
			foreach(IAppInstance AppInstance in TestInstance.ClientApps)
			{
				if (AppInstance != null)
				{
					IDeviceUsageReporter.RecordComment(AppInstance.Device.Name, (UnrealTargetPlatform)AppInstance.Device.Platform, IDeviceUsageReporter.EventType.Device, Context.Options.JobDetails);
					IDeviceUsageReporter.RecordComment(AppInstance.Device.Name, (UnrealTargetPlatform)AppInstance.Device.Platform, IDeviceUsageReporter.EventType.Test, this.GetType().Name);
				}
			}
			
			

			// track the overall session time
			if (SessionStartTime == DateTime.MinValue)
			{
				SessionStartTime = DateTime.Now;
			}

			if (TestInstance != null)
			{
				// Update these for the executor
				MaxDuration = Config.MaxDuration;
				MaxDurationReachedResult = Config.MaxDurationReachedResult;
				MaxRetries = Config.MaxRetries;
				MarkTestStarted();
			}
			
			return TestInstance != null;
		}

		public virtual void PopulateCommandlineInfo()
		{
			SupportedParameters.Add(new GauntletParamDescription()
			{
				ParamName = "nomcp",
				TestSpecificParam = false,
				ParamDesc = "Run test without an mcp backend",
				DefaultValue = "false"
			});
			SupportedParameters.Add(new GauntletParamDescription()
			{
				ParamName = "ResX",
				InputFormat = "1280",
				Required = false,
				TestSpecificParam = false,
				ParamDesc = "Horizontal resolution for the game client.",
				DefaultValue = "1280"
			});
			SupportedParameters.Add(new GauntletParamDescription()
			{
				ParamName = "ResY",
				InputFormat = "720",
				Required = false,
				TestSpecificParam = false,
				ParamDesc = "Vertical resolution for the game client.",
				DefaultValue = "720"
			});
			SupportedParameters.Add(new GauntletParamDescription()
			{
				ParamName = "FailOnEnsure",
				Required = false,
				TestSpecificParam = false,
				ParamDesc = "Consider the test a fail if we encounter ensures."
			});
			SupportedParameters.Add(new GauntletParamDescription()
			{
				ParamName = "MaxDuration",
				InputFormat = "600",
				Required = false,
				TestSpecificParam = false,
				ParamDesc = "Time in seconds for test to run before it is timed out",
				DefaultValue = "Test-defined"
			});
		}

		/// <summary>
		/// Cleanup all resources
		/// </summary>
		/// <param name="Node"></param>
		/// <returns></returns>
		public override void CleanupTest()
		{
			if (TestInstance != null)
			{
				TestInstance.Dispose();
				TestInstance = null;
			}

			if (UnrealApp != null)
			{
				UnrealApp.Dispose();
				UnrealApp = null;
			}			
		}

		/// <summary>
		/// Restarts the provided test. Only called if one of our derived
		/// classes requests it via the Status result
		/// </summary>
		/// <param name="Node"></param>
		/// <returns></returns>
		public override bool RestartTest()
		{
			//Reset/Increment artifact output
			SessionArtifacts = Enumerable.Empty<UnrealRoleArtifacts>();
			RoleResults = Enumerable.Empty<UnrealRoleResult>();

			LastAppLogCount = 0;
			LastEditorLogCount = 0;
			LastHeartbeatTime = DateTime.MinValue;
			LastActiveHeartbeatTime = DateTime.MinValue;

			ReserveArtifactPath();
			Log.Info("UnrealTestNode.ReStartTest Calling CreateDirectory for artifacts at {0}", ArtifactPath);
			Directory.CreateDirectory(ArtifactPath);

			// Relaunch the test
			TestInstance = UnrealApp.RestartSession();

			bool bWasRestarted = (TestInstance != null);
			if (bWasRestarted)
			{
				MarkTestStarted();
			}

			return bWasRestarted;
		}

		/// <summary>
		/// Periodically called while the test is running. A chance for tests to examine their
		/// health, log updates etc. Base classes must call this or take all responsibility for
		/// setting Status as necessary
		/// </summary>
		/// <returns></returns>
		public override void TickTest()
		{
			IAppInstance App = null;
			string AppInfoPrefix = "App";
			if (TestInstance.ClientApps == null)
			{
				App = TestInstance.ServerApp;
				AppInfoPrefix = "Server";
			}
			else
			{
				if (TestInstance.ClientApps.Length > 0)
				{
					App = TestInstance.ClientApps.First();
					AppInfoPrefix = "Client";
				}
			}

			List<string> LogCategories = new List<string>();
			LogCategories.Add("Gauntlet");
			{
				// get the categories used to monitor process (this needs rethought).
				IEnumerable<string> HeartbeatCategories = GetHeartbeatLogCategories().Union(GetCachedConfiguration().LogCategoriesForEvents);
				LogCategories.AddRange(HeartbeatCategories);
			}
			LogCategories = LogCategories.Distinct().ToList();


			if (App != null)
			{
				UnrealLogParser Parser = new UnrealLogParser(App.StdOut);
				List<string> TestLines = new List<string>();
				// ONLY ADD RANGE ONCE. Ordering is important and will be skewed if multiple ranges are added which can skew how logs are pulled out.
				TestLines.AddRange(Parser.GetLogChannels(LogCategories, true));
								
				for (int i = LastAppLogCount; i < TestLines.Count(); i++)
				{
					Log.Info(string.Format("{0}: {1}", AppInfoPrefix, TestLines[i]));

					if (Regex.IsMatch(TestLines[i], @".*GauntletHeartbeat\: Active.*"))
					{
						LastHeartbeatTime = DateTime.Now;
						LastActiveHeartbeatTime = DateTime.Now;
					}
					else if (Regex.IsMatch(TestLines[i], @".*GauntletHeartbeat\: Idle.*"))
					{
						LastHeartbeatTime = DateTime.Now;
					}
				}

				LastAppLogCount = TestLines.Count();

				// Detect missed heartbeats and fail the test
				CheckHeartbeat();
			}

			IAppInstance EditorApp = TestInstance.EditorApp;
			if (EditorApp != null)
			{
				UnrealLogParser Parser = new UnrealLogParser(EditorApp.StdOut);
				List<string> TestLines = new List<string>();
				// ONLY ADD RANGE ONCE. Ordering is important and will be skewed if multiple ranges are added which can skew how logs are pulled out.
				TestLines.AddRange(Parser.GetLogChannels(LogCategories, true));

				for (int i = LastEditorLogCount; i < TestLines.Count(); i++)
				{
					Log.Info(string.Format("Editor: {0}", TestLines[i]));
				}

				LastEditorLogCount = TestLines.Count();
			}


			// Check status and health after updating logs
			if (GetTestStatus() == TestStatus.InProgress && IsTestRunning() == false)
			{
				MarkTestComplete();
			}
		}

		/// <summary>
		/// This class is here to provide compatiblity 
		/// </summary>
		/// <param name="WasCancelled"></param>
		protected virtual void StopTest(bool WasCancelled)
		{
			StopTest(WasCancelled ? StopReason.MaxDuration : StopReason.Completed);
		}

		/// <summary>
		/// Called when a test has completed. By default saves artifacts and calles CreateReport
		/// </summary>
		/// <param name="Result"></param>
		/// <returns></returns>
		public override void StopTest(StopReason InReason)
		{
			base.StopTest(InReason);

			// Shutdown the instance so we can access all files, but do not null it or shutdown the UnrealApp because we still need
			// access to these objects and their resources! Final cleanup is done in CleanupTest()
			TestInstance.Shutdown();

			try
			{
				Log.Info("Saving artifacts to {0}", ArtifactPath);
				// run create dir again just in case the already made dir was cleaned up by another buildfarm job or something similar.
				Directory.CreateDirectory(ArtifactPath);
				Utils.SystemHelpers.MarkDirectoryForCleanup(ArtifactPath);

				SessionArtifacts = SaveRoleArtifacts(ArtifactPath);

				// call legacy version
				SaveArtifacts_DEPRECATED(ArtifactPath);
			}
			catch (Exception Ex)
			{
				Log.Warning("Failed to save artifacts. {0}", Ex);
			}

			try
			{
				// Artifacts have been saved, release devices back to pool for other tests to use
				UnrealApp.UnrealDeviceReservation.ReleaseDevices();
			}
			catch (Exception Ex)
			{
				Log.Warning("Failed to release devices. {0}", Ex);
			}


			// Create results from all roles from these artifacts
			RoleResults = CreateRoleResultsFromArtifacts(InReason, SessionArtifacts);

			string Message = string.Empty;
			ITestReport Report = null;

			try
			{
				// Check if the deprecated signature is overriden, call it anyway if that the case.
				var OldSignature = new[] { typeof(TestResult), typeof(UnrealTestContext), typeof(UnrealBuildSource), typeof(IEnumerable<UnrealRoleResult>), typeof(string) };
				var Simplifiedignature = new[] { typeof(TestResult) };
				if (!Utils.InterfaceHelpers.HasOverriddenMethod(this.GetType(), "CreateReport", Simplifiedignature) && Utils.InterfaceHelpers.HasOverriddenMethod(this.GetType(), "CreateReport", OldSignature))
				{
					Report = CreateReport(GetTestResult(), Context, Context.BuildInfo, RoleResults, ArtifactPath);
				}
				else
				{
					Report = CreateReport(GetTestResult());
				}
			}
			catch (Exception Ex)
			{
				CreateReportFailed = true;
				Message = Ex.Message;				
				Log.Warning("Failed to save completion report. {0}", Ex);
			}

			if (CreateReportFailed)
			{
				try
				{
					HandleCreateReportFailure(Context, Message);
				}
				catch (Exception Ex)
				{
					Log.Warning("Failed to handle completion report failure. {0}", Ex);
				}
			}

			try
			{
				// Check if the deprecated signature is overriden, call it anyway if that the case.
				var DeprecatedSignature = new[] { typeof(TestResult), typeof(UnrealTestContext), typeof(UnrealBuildSource), typeof(IEnumerable<UnrealRoleArtifacts>), typeof(string) };
				if (Utils.InterfaceHelpers.HasOverriddenMethod(this.GetType(), "SubmitToDashboard", DeprecatedSignature))
				{
					SubmitToDashboard(GetTestResult(), Context, Context.BuildInfo, SessionArtifacts, ArtifactPath);
				}

				if (Report != null) { SubmitToDashboard(Report); }
			}
			catch (Exception Ex)
			{
				Log.Warning("Failed to submit results to dashboard. {0}", Ex);
			}
		}

		/// <summary>
		/// Called when report creation fails, by default logs warning with failure message
		/// </summary>
		protected virtual void HandleCreateReportFailure(UnrealTestContext Context, string Message = "")
		{
			if (string.IsNullOrEmpty(Message))
			{
				Message = string.Format("See Gauntlet.log for details");
			}

			Log.Warning("CreateReport Failed: {0}", Message);
		}

		/// <summary>
		/// Whether creating the test report failed
		/// </summary>
		public bool CreateReportFailed { get; protected set; }

		/// <summary>
		/// Display all of the defined commandline information for this test.
		/// Will display generic gauntlet params as well if -help=full is passed in.
		/// </summary>
		public override void DisplayCommandlineHelp()
		{
			Log.Info(string.Format("--- Available Commandline Parameters for {0} ---", Name));
			Log.Info("--------------------");
			Log.Info("TEST-SPECIFIC PARAMS");
			Log.Info("--------------------");
			foreach (GauntletParamDescription ParamDesc in SupportedParameters)
			{
				if (ParamDesc.TestSpecificParam)
				{
					Log.Info(ParamDesc.ToString());
				}
			}
			if (Context.TestParams.ParseParam("listallargs"))
			{
				Log.Info("\n--------------");
				Log.Info("GENERIC PARAMS");
				Log.Info("--------------");
				foreach (GauntletParamDescription ParamDesc in SupportedParameters)
				{
					if (!ParamDesc.TestSpecificParam)
					{
						Log.Info(ParamDesc.ToString());
					}
				}
			}
			else
			{
				Log.Info("\nIf you need information on base Gauntlet arguments, use -listallargs\n\n");
			}
			if (SampleCommandlines.Count > 0)
			{
				Log.Info("\n-------------------");
				Log.Info("SAMPLE COMMANDLINES");
				Log.Info("-------------------");
				foreach (KeyValuePair<string, string> SampleCommandline in SampleCommandlines)
				{
					Log.Info(SampleCommandline.Key);
					Log.Info("");
					Log.Info(SampleCommandline.Value);
					Log.Info("-------------------");
				}
			}

		}

		/// <summary>
		/// Optional function that is called on test completion and gives an opportunity to create a report. The returned class will later be passed to
		/// SubmitToDashboard if submisson of results is enabled
		/// </summary>
		/// <param name="Result">Test result</param>
		/// <param name="Context">Context that describes the environment of the test</param>
		/// <param name="Build">Build being tested</param>
		/// <param name="RoleResults">Results from each role in the test</param>
		/// <param name="ArtifactPath">Path to where artifacts from each role are saved</param>
		/// <returns></returns>
		public virtual ITestReport CreateReport(TestResult Result, UnrealTestContext Context, UnrealBuildSource Build, IEnumerable<UnrealRoleResult> RoleResults, string ArtifactPath)
		{
			if (GetConfiguration().WriteTestResultsForHorde)
			{
				// write test report for Horde
				HordeReport.SimpleTestReport HordeTestReport = CreateSimpleReportForHorde(Result);
				return HordeTestReport;
			}

			return null;
		}

		/// <summary>
		/// Optional function that is called on test completion and gives an opportunity to create a report
		/// </summary>
		/// <param name="Result"></param>
		/// <returns>ITestReport</returns>
		public virtual ITestReport CreateReport(TestResult Result)
		{
			if (GetConfiguration().WriteTestResultsForHorde)
			{
				// write test report for Horde
				HordeReport.SimpleTestReport HordeTestReport = CreateSimpleReportForHorde(Result);
				return HordeTestReport;
			}

			return null;
		}

		/// <summary>
		/// Generate a Simple Test Report from the results of this test
		/// </summary>
		/// <param name="Result"></param>
		protected virtual HordeReport.SimpleTestReport CreateSimpleReportForHorde(TestResult Result)
		{
			HordeReport.SimpleTestReport HordeTestReport = new HordeReport.SimpleTestReport();
			HordeTestReport.ReportCreatedOn = DateTime.Now.ToString();
			HordeTestReport.TotalDurationSeconds = (float) (DateTime.Now - SessionStartTime).TotalSeconds;
			HordeTestReport.Description = Context.ToString();
			HordeTestReport.URLLink = GetURLLink();
			HordeTestReport.Errors.AddRange(GetErrorsAndAbnornalExits());
			if (!string.IsNullOrEmpty(CancellationReason))
			{
				HordeTestReport.Errors.Add(CancellationReason);
			}
			HordeTestReport.Warnings.AddRange(GetWarnings());
			HordeTestReport.HasSucceeded = !(Result == TestResult.Failed || Result == TestResult.TimedOut || HordeTestReport.Errors.Count > 0);
			if (HordeTestReport.Errors.Count > 0 && Result == TestResult.Passed)
			{
				SetUnrealTestResult(TestResult.Failed);
			}
			HordeTestReport.Status = GetTestResult().ToString();
			string HordeArtifactPath = string.IsNullOrEmpty(GetConfiguration().HordeArtifactPath) ? HordeReport.DefaultArtifactsDir : GetConfiguration().HordeArtifactPath;
			HordeTestReport.SetOutputArtifactPath(HordeArtifactPath);
			if (SessionArtifacts != null)
			{
				foreach (UnrealRoleResult RoleResult in RoleResults)
				{
					string LogName = Path.GetFullPath(RoleResult.Artifacts.LogPath).Replace(Path.GetFullPath(Context.Options.LogDir), "").TrimStart(Path.DirectorySeparatorChar);
					HordeTestReport.AttachArtifact(RoleResult.Artifacts.LogPath, LogName);

					UnrealLog LogSummary = RoleResult.LogSummary;
					if (LogSummary.Errors.Count() > 0)
					{
						HordeTestReport.Warnings.Add(
							string.Format(
								"Log Parsing: FatalErrors={0}, Ensures={1}, Errors={2}, Warnings={3}, Log={4}",
								(LogSummary.FatalError != null ? 1 : 0), LogSummary.Ensures.Count(), LogSummary.Errors.Count(), LogSummary.Warnings.Count(), LogName
							)
						);
					}
				}
			}
			// Metadata
			SetReportMetadata(HordeTestReport, GetConfiguration().RequiredRoles.Values.SelectMany(V => V));

			return HordeTestReport;
		}

		/// <summary>
		/// Generate report from Unreal Automated Test Results
		/// </summary>
		/// <param name="UnrealAutomatedTestReportPath"></param>
		/// <param name="ReportURL"></param>
		/// <returns>ITestReport</returns>
		public virtual ITestReport CreateUnrealEngineTestPassReport(string UnrealAutomatedTestReportPath, string ReportURL)
		{
			string JsonReportPath = Path.Combine(UnrealAutomatedTestReportPath, "index.json");
			if (File.Exists(JsonReportPath))
			{
				string HordeArtifactPath = GetConfiguration().HordeArtifactPath;
				Log.Verbose("Reading json Unreal Automated test report from {0}", JsonReportPath);
				UnrealAutomatedTestPassResults JsonTestPassResults = UnrealAutomatedTestPassResults.LoadFromJson(JsonReportPath);
				if (JsonTestPassResults.InProcess > 0)
				{
					// The test pass did not run completely
					Log.Verbose("Found in-process tests: {0}", JsonTestPassResults.InProcess);
					// Get any critical error and push it to json report and resave it.
					if (RoleResults != null)
					{
						UnrealLog.CallstackMessage FatalError = null;
						foreach (UnrealRoleResult Result in RoleResults)
						{
							if (Result.LogSummary.FatalError != null)
							{
								FatalError = Result.LogSummary.FatalError;
								break;
							}
						}
						if (FatalError != null)
						{
							var Test = JsonTestPassResults.Tests.FirstOrDefault((T => T.State == TestStateType.InProcess));
							if (!String.IsNullOrEmpty(Test.TestDisplayName))
							{
								Test.AddError("Engine encountered a critical failure. \n" + FatalError.FormatForLog());
								JsonTestPassResults.WriteToJson(JsonReportPath);
							}
						}
					}
				}
				if (JsonTestPassResults.NotRun > 0)
				{
					// The test pass did not run at all
					Log.Verbose("Found not-run tests: {0}", JsonTestPassResults.NotRun);
					bool HasTimeout = RoleResults != null && RoleResults.Where(R => R.ProcessResult == UnrealProcessResult.TimeOut).Any();
					if (GetConfiguration().ResumeOnCriticalFailure && !HasTimeout)
					{
						if (Retries < MaxRetries)
						{
							Retries++;
							// Reschedule test to resume from last 'in-process' test.
							SetUnrealTestResult(TestResult.WantRetry);
							// Attach current artifacts to Horde output
							if (SessionArtifacts != null)
							{
								HordeReport.SimpleTestReport TempReport = new HordeReport.SimpleTestReport();
								TempReport.SetOutputArtifactPath(HordeArtifactPath);
								foreach (UnrealRoleArtifacts Artifact in SessionArtifacts)
								{
									string LogName = Path.GetFullPath(Artifact.LogPath).Replace(Path.GetFullPath(Context.Options.LogDir), "").TrimStart(Path.DirectorySeparatorChar);
									TempReport.AttachArtifact(Artifact.LogPath, LogName);
								}
							}
							// Discard the report as we are going to do another pass.
							return null;
						}
						else
						{
							Log.Error("Reach maximum of retries({0}) to resume on critical failure!", Retries);
						}
					}
				}
				// Convert test results for Horde
				HordeReport.UnrealEngineTestPassResults HordeTestPassResults = HordeReport.UnrealEngineTestPassResults.FromUnrealAutomatedTests(JsonTestPassResults, UnrealAutomatedTestReportPath, ReportURL);
				HordeTestPassResults.CopyTestResultsArtifacts(HordeArtifactPath);
				// Metadata
				// With UE Test Automation, we care only for one role.
				var MainRole = new List<UnrealTestRole>() { GetConfiguration().GetMainRequiredRole() };
				SetReportMetadata(HordeTestPassResults, MainRole);
				// Attached test Artifacts
				if (SessionArtifacts != null)
				{
					foreach (UnrealRoleArtifacts Artifact in SessionArtifacts)
					{
						string LogName = Path.GetFullPath(Artifact.LogPath).Replace(Path.GetFullPath(Context.Options.LogDir), "").TrimStart(Path.DirectorySeparatorChar);
						HordeTestPassResults.AttachArtifact(Artifact.LogPath, LogName);
					}
				}
				return HordeTestPassResults;
			}
			else
			{
				Log.Warning("Could not find Unreal Automated test report at {0}. Reverting to base report.", JsonReportPath);
				return CreateSimpleReportForHorde(GetTestResult());
			}
		}

		/// <summary>
		/// Set Metadata on ITestReport
		/// </summary>
		/// <param name="Report"></param>
		protected virtual void SetReportMetadata(ITestReport Report, IEnumerable<UnrealTestRole> Roles)
		{
			var AllRoleTypes = Roles.Select(R =>  R.Type);
			var AllRoleContexts = Roles.Select(R => Context.GetRoleContext(R.Type));
			Report.SetMetadata("Platform", string.Join("+", AllRoleContexts.Select(R => R.Platform.ToString()).Distinct().OrderBy(P => P)));
			Report.SetMetadata("BuildType", string.Join("+", AllRoleTypes.Select(R => R.ToString()).Distinct().OrderBy(B => B)));
			Report.SetMetadata("Configuration", string.Join("+", AllRoleContexts.Select(R => R.Configuration.ToString()).Distinct().OrderBy(C => C)));
			Report.SetMetadata("Project", Context.BuildInfo.ProjectName);
		}

		/// <summary>
		/// DEPRECATED Optional function that is called on test completion and gives an opportunity to submit a report to a Dashboard
		/// </summary>
		/// <param name="Result"></param>
		/// <param name="Context"></param>
		/// <param name="Build"></param>
		/// <param name="Artifacts"></param>
		/// <param name="ArtifactPath"></param>
		/// <returns></returns>
		public virtual void SubmitToDashboard(TestResult Result, UnrealTestContext Context, UnrealBuildSource Build, IEnumerable<UnrealRoleArtifacts> Artifacts, string ArtifactPath)
		{
		}

		/// <summary>
		/// Optional function that is called on test completion and gives an opportunity to submit a report to a Dashboard
		/// </summary>
		/// <param name="Report"></param>
		public virtual void SubmitToDashboard(ITestReport Report)
		{
			if (GetConfiguration().WriteTestResultsForHorde)
			{
				// write test data collection for Horde
				string HordeTestDataKey = string.IsNullOrEmpty(GetConfiguration().HordeTestDataKey) ? Name + " " + Context.ToString() : GetConfiguration().HordeTestDataKey;
				string HordeTestDataFilePath = Path.Combine(
					string.IsNullOrEmpty(GetConfiguration().HordeTestDataPath) ? HordeReport.DefaultTestDataDir : GetConfiguration().HordeTestDataPath,
					FileUtils.SanitizeFilename(Name) + ".TestData.json"
				);
				HordeReport.TestDataCollection HordeTestDataCollection = new HordeReport.TestDataCollection();
				HordeTestDataCollection.AddNewTestReport(HordeTestDataKey, Report);
				HordeTestDataCollection.WriteToJson(HordeTestDataFilePath, true);
			}
			if (!string.IsNullOrEmpty(GetConfiguration().PublishTelemetryTo) && Report is ITelemetryReport Telemetry)
			{
				IEnumerable<TelemetryData> DataRows = Telemetry.GetAllTelemetryData();
				if (DataRows != null)
				{
					IDatabaseConfig<TelemetryData> DBConfig = DatabaseConfigManager<TelemetryData>.GetConfigByName(GetConfiguration().PublishTelemetryTo);
					if (DBConfig != null)
					{
						DBConfig.LoadConfig(GetConfiguration().DatabaseConfigPath);
						IDatabaseDriver<TelemetryData> DB = DBConfig.GetDriver();
						Log.Verbose("Submitting telemetry data to {0}", DB.ToString());

						UnrealTelemetryContext TestContext = new UnrealTelemetryContext();
						TestContext.SetProperty("ProjectName", Context.BuildInfo.ProjectName);
						TestContext.SetProperty("Branch", Context.BuildInfo.Branch);
						TestContext.SetProperty("Changelist", Context.BuildInfo.Changelist);
						var RoleType = GetConfiguration().GetMainRequiredRole().Type;
						var Role = Context.GetRoleContext(RoleType);
						TestContext.SetProperty("Platform", Role.Platform);
						TestContext.SetProperty("Configuration", string.Format("{0} {1}", RoleType, Role.Configuration));

						DB.SubmitDataItems(DataRows, TestContext);
					}
					else
					{
						Log.Warning("Got telemetry data, but database configuration is unknown '{0}'.", GetConfiguration().PublishTelemetryTo);
					}
				}
			}
		}

		/// <summary>
		/// Called to request that the test save all artifacts from the completed test to the specified 
		/// output path. By default the app will save all logs and crash dumps
		/// </summary>
		/// <param name="Completed"></param>
		/// <param name="Node"></param>
		/// <param name="OutputPath"></param>
		/// <returns></returns>
		public virtual void SaveArtifacts_DEPRECATED(string OutputPath)
		{
			// called for legacy reasons
		}

		/// <summary>
		/// Called to request that the test save all artifacts from the completed test to the specified 
		/// output path. By default the app will save all logs and crash dumps
		/// </summary>
		/// <param name="Completed"></param>
		/// <param name="Node"></param>
		/// <param name="OutputPath"></param>
		/// <returns></returns>
		public virtual IEnumerable<UnrealRoleArtifacts> SaveRoleArtifacts(string OutputPath)
		{
			return UnrealApp.SaveRoleArtifacts(Context, TestInstance, ArtifactPath);
		}

		/// <summary>
		/// Parses the provided artifacts to determine the cause of an exit and whether it was abnormal
		/// </summary>
		/// <param name="InArtifacts"></param>
		/// <param name="Reason"></param>
		/// <param name="WasAbnormal"></param>
		/// <returns></returns>
		protected virtual UnrealProcessResult GetExitCodeAndReason(StopReason InReason, UnrealLog InLog, UnrealRoleArtifacts InArtifacts, out string ExitReason, out int ExitCode)
		{
			// first check for fatal issues
			if (InLog.FatalError != null)
			{
				ExitReason = "Process encountered fatal error";
				ExitCode = -1;
				return UnrealProcessResult.EncounteredFatalError;
			}

			// Catch failed engine init. Early issues can result in the engine exiting with hard to diagnose reasons
			if (InLog.EngineInitialized == false)
			{
				ExitReason = string.Format("Engine initialization failed");
				ExitCode = -1;
				return UnrealProcessResult.InitializationFailure;
			}

			// If the test considers ensures as fatal, fail here
			if (CachedConfig.FailOnEnsures && InLog.Ensures.Count() > 0)
			{
				ExitReason = string.Format("Process encountered {0} Ensures", InLog.Ensures.Count());
				ExitCode = -1;
				return UnrealProcessResult.EncounteredEnsure;
			}

			// Gauntlet killed the process. This can be valid in many scenarios (e.g. shutting down an ancillary 
			// process, but if there was a timeout it will be handled at a higher level
			if (InArtifacts.AppInstance.WasKilled)
			{
				if (InReason == StopReason.MaxDuration)
				{
					ExitReason = "Process was killed by Gauntlet due to a timeout";
					ExitCode = -1;
					return UnrealProcessResult.TimeOut;
				}
				else
				{
					ExitReason = string.Format("Process was killed by Gauntlet [Reason={0}]", InReason.ToString());
					ExitCode = 0;
					return UnrealProcessResult.ExitOk;
				}
			}

			// If we found a valid exit code with test markup, return it
			if (InLog.HasTestExitCode)
			{
				ExitReason = string.Format("Tests exited with error code {0}", InLog.TestExitCode);
				ExitCode = InLog.TestExitCode;
				return ExitCode == 0 ? UnrealProcessResult.ExitOk : UnrealProcessResult.TestFailure;
			}

			// Engine exit was requested with no visible fatal error
			if (InLog.RequestedExit)
			{
				// todo - need join cleanup with UE around RE due to errors
				ExitReason = string.Format("Exit was requested: {0}", InLog.RequestedExitReason);
				ExitCode = 0;
				return UnrealProcessResult.ExitOk;
			}

			bool WasGauntletTest = InArtifacts.SessionRole.CommandLine.ToLower().Contains("-gauntlet");
			// ok, process appears to have exited for no good reason so try to divine a result...
			if (WasGauntletTest)
			{
				if (InLog.HasTestExitCode == false)
				{
					Log.Verbose("Role {0} had 0 exit code but used Gauntlet and no TestExitCode was found. Assuming failure", InArtifacts.SessionRole.RoleType);
					ExitReason = "Process terminated prematurely! No test result from Gauntlet controller";
					ExitCode = -1;
					return UnrealProcessResult.TestFailure;
				}
			}

			// AG TODO - do we still need this?
			// Normal exits from server are not ok if we had clients running!
			/*if (ExitCode == 0 && InArtifacts.SessionRole.RoleType.IsServer())
			{
				bool ClientsKilled = SessionArtifacts.Any(A => A.AppInstance.WasKilled && A.SessionRole.RoleType.IsClient());

				if (ClientsKilled)
				{
					ExitCode = -1;
					ExitReason = "Server exited while clients were running";
				}
			}*/
			
			// The process is gone but we don't know why. This is likely bad and signifies an unhandled or undiagnosed error
			ExitReason = "app exited with code 0";
			ExitCode = -1;
			return UnrealProcessResult.Unknown;
		}

		/// <summary>
		/// Creates an EventList from the artifacts for the specified role. By default this will be asserts (errors), ensures (warnings)
		/// from the log, plus any log entries from categories that are the list returned by GetMonitoredLogCategories(). Nodes can also set
		/// their behavior flags to elevate *all* warnings/errors
		/// </summary>
		/// <param name="InReason"></param>
		/// <param name="InRoleArtifacts"></param>
		/// <param name="InLog"></param>
		/// <returns></returns>
		protected virtual IEnumerable<UnrealTestEvent> CreateEventListFromArtifact(StopReason InReason, UnrealRoleArtifacts InRoleArtifacts, UnrealLog InLog)
		{
			List<UnrealTestEvent> EventList = new List<UnrealTestEvent>();

			// Create events for any fatal errors in the log
			if (InLog.FatalError != null)
			{
				UnrealTestEvent FatalEvent = new UnrealTestEvent(EventSeverity.Fatal, InLog.FatalError.Message, Enumerable.Empty<string>(), InLog.FatalError);
				EventList.Add(FatalEvent);
			}

			// Create events for any ensures
			foreach (UnrealLog.CallstackMessage Ensure in InLog.Ensures)
			{
				UnrealTestEvent EnsureEvent = new UnrealTestEvent(EventSeverity.Warning, Ensure.Message, Enumerable.Empty<string>(), Ensure);
				EventList.Add(EnsureEvent);
			}

			HashSet<string> MonitoredCategorySet = new HashSet<string>();

			foreach(string Category in GetCachedConfiguration().LogCategoriesForEvents)
			{
				MonitoredCategorySet.Add(Category);
			}

			bool TrackAllWarnings = GetCachedConfiguration().ShowWarningsInSummary || Flags.HasFlag(BehaviorFlags.PromoteWarnings);
			bool TrackAllErrors = GetCachedConfiguration().ShowErrorsInSummary || Flags.HasFlag(BehaviorFlags.PromoteErrors);

			// now look at the log. Add events for warnings/errors if the category is monitored or if this test is flagged to 
			// promote all warnings/errors
			foreach (UnrealLog.LogEntry Entry in InLog.LogEntries)
			{
				bool IsMonitored = MonitoredCategorySet.Contains(Entry.Category);

				if (Entry.Level == UnrealLog.LogLevel.Warning && 
					(IsMonitored || TrackAllWarnings))
				{
					EventList.Add(new UnrealTestEvent(EventSeverity.Warning, Entry.ToString(), Enumerable.Empty<string>()));
				}

				if (Entry.Level == UnrealLog.LogLevel.Error &&
					(IsMonitored || TrackAllErrors))
				{
					EventList.Add(new UnrealTestEvent(EventSeverity.Error, Entry.ToString(), Enumerable.Empty<string>()));
				}
			}

			return EventList;
		}


		/// <summary>
		/// Returns a RoleResult, a representation of this roles result from the test, for the provided artifact
		/// </summary>
		/// <param name="InRoleArtifacts"></param>
		/// <returns></returns>
		protected virtual UnrealRoleResult CreateRoleResultFromArtifact(StopReason InReason, UnrealRoleArtifacts InRoleArtifacts)
		{
			int ExitCode;
			string ExitReason;

			UnrealLog LogSummary = CreateLogSummaryFromArtifact(InRoleArtifacts);
	
			// Give ourselves (and derived classes) a chance to analyze what happened
			UnrealProcessResult ProcessResult = GetExitCodeAndReason(InReason, LogSummary, InRoleArtifacts, out ExitReason, out ExitCode);

			IEnumerable<UnrealTestEvent> EventList = CreateEventListFromArtifact(InReason, InRoleArtifacts, LogSummary);

			// if the test is stopping for a reason other than completion, mark this as failing incase derived classes
			// don't do the right thing
			if (InReason == StopReason.MaxDuration)
			{
				ProcessResult = UnrealProcessResult.TimeOut;
				ExitCode = -1;
			}

			return new UnrealRoleResult(ProcessResult, ExitCode, ExitReason, LogSummary, InRoleArtifacts, EventList); 
		}

		/// <summary>
		/// Returns a log summary from the provided artifacts. 
		/// </summary>
		/// <param name="InArtifacts"></param>
		/// <returns></returns>
		protected virtual UnrealLog CreateLogSummaryFromArtifact(UnrealRoleArtifacts InArtifacts)
		{
			return new UnrealLogParser(InArtifacts.AppInstance.StdOut).GetSummary();
		}

		/// <summary>
		/// Returns a list of all results for the roles involved in this test by calling CreateRoleResultFromArtifact for all
		/// artifacts in the list
		/// </summary>
		/// <param name="InAllArtifacts"></param>
		/// <returns></returns>
		protected virtual IEnumerable<UnrealRoleResult> CreateRoleResultsFromArtifacts(StopReason InReason, IEnumerable<UnrealRoleArtifacts> InAllArtifacts)
		{
			return InAllArtifacts.Select(A => CreateRoleResultFromArtifact(InReason, A)).ToArray();
		}


		private void CheckHeartbeat()
		{
			if (CachedConfig == null 
				|| CachedConfig.DisableHeartbeatTimeout
				|| CachedConfig.HeartbeatOptions.bExpectHeartbeats == false
				|| GetTestStatus() != TestStatus.InProgress)
			{
				return;
			}

			UnrealHeartbeatOptions HeartbeatOptions = CachedConfig.HeartbeatOptions;

			// First active heartbeat has not happened yet and timeout before first active heartbeat is enabled
			if (LastActiveHeartbeatTime == DateTime.MinValue && HeartbeatOptions.TimeoutBeforeFirstActiveHeartbeat > 0)
			{
				double SecondsSinceSessionStart = DateTime.Now.Subtract(SessionStartTime).TotalSeconds;
				if (SecondsSinceSessionStart > HeartbeatOptions.TimeoutBeforeFirstActiveHeartbeat)
				{
					Log.Error("{0} seconds have passed without detecting the first active Gauntlet heartbeat.", HeartbeatOptions.TimeoutBeforeFirstActiveHeartbeat);
					MarkTestComplete();
					SetUnrealTestResult(TestResult.TimedOut);
				}
			}

			// First active heartbeat has happened and timeout between active heartbeats is enabled
			if (LastActiveHeartbeatTime != DateTime.MinValue && HeartbeatOptions.TimeoutBetweenActiveHeartbeats > 0)
			{
				double SecondsSinceLastActiveHeartbeat = DateTime.Now.Subtract(LastActiveHeartbeatTime).TotalSeconds;
				if (SecondsSinceLastActiveHeartbeat > HeartbeatOptions.TimeoutBetweenActiveHeartbeats)
				{
					Log.Error("{0} seconds have passed without detecting any active Gauntlet heartbeats.", HeartbeatOptions.TimeoutBetweenActiveHeartbeats);
					MarkTestComplete();
					SetUnrealTestResult(TestResult.TimedOut);
				}
			}

			// First heartbeat has happened and timeout between heartbeats is enabled
			if (LastHeartbeatTime != DateTime.MinValue && HeartbeatOptions.TimeoutBetweenAnyHeartbeats > 0)
			{
				double SecondsSinceLastHeartbeat = DateTime.Now.Subtract(LastHeartbeatTime).TotalSeconds;
				if (SecondsSinceLastHeartbeat > HeartbeatOptions.TimeoutBetweenAnyHeartbeats)
				{
					Log.Error("{0} seconds have passed without detecting any Gauntlet heartbeats.", HeartbeatOptions.TimeoutBetweenAnyHeartbeats);
					MarkTestComplete();
					SetUnrealTestResult(TestResult.TimedOut);
				}
			}
		}

		/// <summary>
		/// Returns a hash that represents the results of a role. Should be 0 if no fatal errors or ensures
		/// </summary>
		/// <param name="InArtifacts"></param>
		/// <returns></returns>
		protected virtual string GetRoleResultHash(UnrealRoleResult InResult)
		{
			const int MaxCallstackLines = 10;			

			UnrealLog LogSummary = InResult.LogSummary;

			string TotalString = "";

			//Func<int, string> ComputeHash = (string Str) => { return Hasher.ComputeHash(Encoding.UTF8.GetBytes(Str)); };
			
			if (LogSummary.FatalError != null)
			{				
				TotalString += string.Join("\n", LogSummary.FatalError.Callstack.Take(MaxCallstackLines));
				TotalString += "\n";
			}

			foreach (var Ensure in LogSummary.Ensures)
			{
				TotalString += string.Join("\n", Ensure.Callstack.Take(MaxCallstackLines));
				TotalString += "\n";
			}

			string Hash = Hasher.ComputeHash(TotalString);

			return Hash;
		}

		/// <summary>
		/// Returns a hash that represents the failure results of this test. If the test failed this should be an empty string
		/// </summary>
		/// <returns></returns>
		protected virtual string GetTestResultHash()
		{
			IEnumerable<string> RoleHashes = RoleResults.Select(R => GetRoleResultHash(R)).OrderBy(S => S);

			RoleHashes = RoleHashes.Where(S => S.Length > 0 && S != "0");

			string Combined = string.Join("\n", RoleHashes);

			string CombinedHash = Hasher.ComputeHash(Combined);

			return CombinedHash;
		}

		
		/// <summary>
		/// Returns a formatted summary of the role that's suitable for displaying
		/// </summary>
		/// <param name="InRoleResult"></param>
		/// <returns></returns>
		protected virtual string GetFormattedRoleSummary(UnrealRoleResult InRoleResult)
		{

			const int MaxLogLines = 10;
			const int MaxCallstackLines = 20;

			UnrealLog LogSummary = InRoleResult.LogSummary;
						
			MarkdownBuilder MB = new MarkdownBuilder();

			UnrealRoleArtifacts RoleArtifacts = InRoleResult.Artifacts;

			MB.H3(string.Format("Role: {0} ({1} {2})", RoleArtifacts.SessionRole.RoleType, RoleArtifacts.SessionRole.Platform, RoleArtifacts.SessionRole.Configuration));

			bool HaveFatalError = LogSummary.FatalError != null;

			// If we have no fatal error use "Error" which Horde will highlight. Otherwise use "Failed" since the fatal error will be below and highligted
			string FailedString = HaveFatalError ? "Failed: " : "Error: ";
			string CompletedString = "Completed: ";

			string RoleState = InRoleResult.ExitCode != 0 && InRoleResult.LogSummary.HasAbnormalExit ? FailedString : CompletedString;

			MB.H4(string.Format("{0} {1} ({2}, ExitCode={3})", RoleState, InRoleResult.Summary, InRoleResult.ProcessResult, InRoleResult.ExitCode));

			// log command line up here for visibility
			//MB.Paragraph(string.Format("CommandLine: {0}", RoleArtifacts.AppInstance.CommandLine));

			MB.UnorderedList(new string[] {
				string.Format("CommandLine: {0}", RoleArtifacts.AppInstance.CommandLine),
				string.Format("Log: {0}", RoleArtifacts.LogPath),
				string.Format("SavedDir: {0}", RoleArtifacts.ArtifactPath),
				LogSummary.FatalError != null ? "Fatal Errors: 1" : null,
				LogSummary.Ensures.Count() > 0 ? string.Format("Ensures: {0}", LogSummary.Ensures.Count()) : null,
				LogSummary.Errors.Count() > 0 ? string.Format("Log Errors: {0}", LogSummary.Errors.Count()) : null,
				LogSummary.Warnings.Count() > 0 ? string.Format("Log Warnings: {0}", LogSummary.Warnings.Count()) : null
			});

			// Separate the events we want to report on
			IEnumerable<UnrealTestEvent> Asserts = InRoleResult.Events.Where(E => E.Severity == EventSeverity.Fatal);
			IEnumerable<UnrealTestEvent> Errors = InRoleResult.Events.Where(E => E.Severity == EventSeverity.Error);
			IEnumerable<UnrealTestEvent> Ensures = InRoleResult.Events.Where(E => E.IsEnsure);
			IEnumerable<UnrealTestEvent> Warnings = InRoleResult.Events.Where(E => E.Severity == EventSeverity.Warning && !E.IsEnsure);

			foreach (UnrealTestEvent Event in Asserts)
			{
				MB.H4(string.Format("Fatal Error: {0}", Event.Summary));

				if (Event.Callstack.Any())
				{
					MB.UnorderedList(Event.Callstack.Take(MaxCallstackLines));

					if (Event.Callstack.Count() > MaxCallstackLines)
					{
						MB.Paragraph("See log for full callstack");
					}
				}
				else
				{
					MB.Paragraph("Could not parse callstack. See log for full callstack");
				}
			}

			foreach (UnrealTestEvent Event in Ensures.Distinct())
			{
				MB.H4(string.Format("Warning: Ensure: {0}", Event.Summary));

				if (Event.Callstack.Any())
				{
					MB.UnorderedList(Event.Callstack.Take(MaxCallstackLines));

					if (Event.Callstack.Count() > MaxCallstackLines)
					{
						MB.Paragraph("See log for full callstack");
					}
				}
				else
				{
					MB.Paragraph("Could not parse callstack. See log for full callstack");
				}
			}
		

			if (Errors.Any())
			{
				var ErrorList = Errors.Select(E => E.Summary).Distinct();
				var PrintedErrorList = ErrorList;

				string TrimStatement = "";

				// too many warnings. If there was an abnormal exit show the last ones as they may be relevant
				if (ErrorList.Count() > MaxLogLines)
				{
					if (LogSummary.HasAbnormalExit)
					{
						PrintedErrorList = ErrorList.Skip(ErrorList.Count() - MaxLogLines);
						TrimStatement = string.Format("(Last {0} of {1} errors)", MaxLogLines, ErrorList.Count());
					}
					else
					{
						PrintedErrorList = ErrorList.Take(MaxLogLines);
						TrimStatement = string.Format("(First {0} of {1} errors)", MaxLogLines, ErrorList.Count());
					}
				}

				MB.H4("Errors:");
				MB.UnorderedList(PrintedErrorList);

				if (!string.IsNullOrEmpty(TrimStatement))
				{
					MB.Paragraph(TrimStatement);
				}
			}

			if (Warnings.Any())
			{
				var WarningList = Warnings.Select(E => E.Summary).Distinct();
				var PrintedWarningList = WarningList;

				string TrimStatement = "";

				// too many warnings. If there was an abnormal exit show the last ones as they may be relevant
				if (WarningList.Count() > MaxLogLines)
				{
					if (LogSummary.HasAbnormalExit)
					{
						PrintedWarningList = WarningList.Skip(WarningList.Count() - MaxLogLines);
						TrimStatement = string.Format("(Last {0} of {1} errors)", MaxLogLines, WarningList.Count());
					}
					else
					{
						PrintedWarningList = WarningList.Take(MaxLogLines);
						TrimStatement = string.Format("(First {0} of {1} errors)", MaxLogLines, WarningList.Count());
					}
				}

				MB.H4("Warnings:");
				MB.UnorderedList(PrintedWarningList);

				if (!string.IsNullOrEmpty(TrimStatement))
				{
					MB.Paragraph(TrimStatement);
				}
			}

			return MB.ToString(); 		
		}

		/// <summary>
		/// Returns the current Pass count for this node
		/// </summary>
		public int GetCurrentPass()
		{
			return CurrentPass;
		}

		/// <summary>
		/// Returns the current total Pass count for this node
		/// </summary>
		/// <returns></returns>
		public int GetNumPasses()
		{
			return NumPasses;
		}

		/// <summary>
		/// Result of the test once completed. Nodes inheriting from us should override
		/// this if custom results are necessary
		/// </summary>
		public sealed override TestResult GetTestResult()
		{
			if (UnrealTestResult == TestResult.Invalid)
			{
				UnrealTestResult = GetUnrealTestResult();
			}

			return UnrealTestResult;
		}

		/// <summary>
		/// Result of the test once completed. Nodes inheriting from us should override
		/// </summary>
		public override void SetTestResult(TestResult testResult)
		{
			UnrealTestResult = testResult;
		}

		/// <summary>
		/// Allows tests to set this at anytime. If not called then GetUnrealTestResult() will be called when
		/// the framework first calls GetTestResult()
		/// </summary>
		/// <param name="Result"></param>
		/// <returns></returns>
		protected void SetUnrealTestResult(TestResult Result)
		{
			if (GetTestStatus() != TestStatus.Complete)
			{
				throw new Exception("SetUnrealTestResult() called while test is incomplete!");
			}

			UnrealTestResult = Result;
		}

		/// <summary>
		/// Return all artifacts that are exited abnormally. An abnormal exit is termed as a fatal error,
		/// crash, assert, or other exit that does not appear to have been caused by completion of a process
		/// </summary>
		/// <returns></returns>
		protected virtual IEnumerable<UnrealRoleResult> GetRolesThatExitedAbnormally()
		{
			if (RoleResults == null)
			{
				Log.Warning("RoleResults was null, unable to check for failures");
				return Enumerable.Empty<UnrealRoleResult>();
			}

			return RoleResults.Where(R => R.ProcessResult != UnrealProcessResult.ExitOk && R.LogSummary.HasAbnormalExit);
		}

		/// <summary>
		/// Return all artifacts that are considered to have caused the test to fail
		/// </summary>
		/// <returns></returns>
		protected virtual IEnumerable<UnrealRoleResult> GetRolesThatFailed()
		{
			if (RoleResults == null)
			{
				Log.Warning("RoleResults was null, unable to check for failures");
				return Enumerable.Empty<UnrealRoleResult>();
			}

			return RoleResults.Where(R => R.ProcessResult != UnrealProcessResult.ExitOk);	
		}

		/// <summary>
		/// THe base implementation considers  considers Classes can override this to implement more custom detection of success/failure than our
		/// log parsing. Not guaranteed to be called if a test is marked complete
		/// </summary>
		/// <returns></returns>in
		protected virtual TestResult GetUnrealTestResult()
		{
			int ExitCode = 0;

			// Let the test try and diagnose things as best it can
			IEnumerable<UnrealRoleResult> FailedRoles = GetRolesThatFailed();

			if (FailedRoles.Any())
			{
				foreach (var Role in FailedRoles)
				{
					Log.Info("Failing test because {0} exited with {1}. ({2})", Role.Artifacts.SessionRole, Role.ExitCode, Role.Summary);
				}
				ExitCode = FailedRoles.FirstOrDefault().ExitCode;
			}

			// If it didn't find an error, overrule it as a failure if the test was cancelled
			if (ExitCode == 0 && WasCancelled)
			{
				return TestResult.Failed;
			}

			return ExitCode == 0 ? TestResult.Passed : TestResult.Failed;
		}

		/// <summary>
		/// Return the header for the test summary. The header is the first block of text and will be
		/// followed by the summary of each individual role in the test
		/// </summary>
		/// <returns></returns>
		protected virtual string GetTestSummaryHeader()
		{
			int FatalErrors = 0;
			int Ensures = 0;
			int Errors = 0;
			int Warnings = 0;

			MarkdownBuilder MB = new MarkdownBuilder();

			bool TestFailed = GetTestResult() != TestResult.Passed;

			// Good/Bad news upfront
			string Prefix = TestFailed ? "Error: " : "";
			string WarningStatement = (HasWarnings && !TestFailed)  ? " With Warnings" : "";
			string ResultString = string.Format("{0}{1} {2}{3} ***", Prefix, this.Name, GetTestResult(), WarningStatement);
			MB.H2(ResultString);
						
			IEnumerable<UnrealRoleResult> SortedRoles = RoleResults.OrderBy(R => R.ProcessResult == UnrealProcessResult.ExitOk);

			// create a quick summary of total failures, ensures, errors, etc. Don't write out errors etc for roles, those will be
			// displayed individually by GetFormattedRoleSummary
			foreach (var RoleResult in SortedRoles)
			{
				string RoleName = RoleResult.Artifacts.SessionRole.RoleType.ToString();

				MB.Paragraph(string.Format("{0} Role: {1} ({2}, ExitCode {3})", RoleName, RoleResult.Summary, RoleResult.ProcessResult, RoleResult.ExitCode));

				FatalErrors += RoleResult.LogSummary.FatalError != null ? 1 : 0;
				Ensures += RoleResult.LogSummary.Ensures.Count();
				Errors += RoleResult.LogSummary.Errors.Count();
				Warnings += RoleResult.LogSummary.Warnings.Count();
			}

			MB.UnorderedList(new string[] {
				string.Format("Context: {0}", Context.ToString()),
				FatalErrors > 0 ? string.Format("FatalErrors: {0}", FatalErrors) : null,
				Ensures > 0 ? string.Format("Ensures: {0}", Ensures) : null,
				Errors > 0 ? string.Format("Log Errors: {0}", Errors) : null,
				Warnings > 0 ? string.Format("Log Warnings: {0}", Warnings) : null,
				string.Format("Result: {0}", GetTestResult())
			});

			if (TestFailed && GetRolesThatFailed().Where(R => R.ProcessResult == UnrealProcessResult.Unknown).Any())
			{
				MB.Paragraph(string.Format("Error: {0} failed due to undiagnosed reasons", this.Name));
				MB.Paragraph("See 'Role Report' below for more details on each role");
			}
			else
			{
				MB.Paragraph("See 'Role Report' below for more details on each role");
			}			

			return MB.ToString();
		}

		/// <summary>
		/// Returns a summary of this test
		/// </summary>
		/// <returns></returns>
		public override string GetTestSummary()
		{
			// Handle case where there aren't any session artifacts, for example with device starvation
			if (SessionArtifacts == null)
			{
				return "NoSummary";
			}

			MarkdownBuilder ReportBuilder = new MarkdownBuilder();			

			// Sort roles so problem ones are at the bottom, just above the summary
			var SortedRoles = RoleResults.OrderBy(R =>
			{
				int Score = 0;

				if (R.ProcessResult != UnrealProcessResult.ExitOk)
				{
					Score += 1000000;
				}

				Score += R.LogSummary.Errors.Count() * 10;
				Score += R.LogSummary.Warnings.Count();

				return Score;
			});		

			// add Summary
			ReportBuilder.HorizontalLine();
			ReportBuilder.H1("Test Summary: " + this.Name);
			ReportBuilder.HorizontalLine();
			ReportBuilder.Append(GetTestSummaryHeader());

			ReportBuilder.HorizontalLine();
			ReportBuilder.H1(string.Format("Role(s): {0}", this.Name));
			ReportBuilder.HorizontalLine();

			// Add a summary of each 
			foreach (var Role in SortedRoles)
			{
				string Summary = GetFormattedRoleSummary(Role);
				ReportBuilder.Append(Summary);
			}

			return ReportBuilder.ToString();
		}
	}
}