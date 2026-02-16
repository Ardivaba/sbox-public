using Sandbox.Internal;
using Sandbox.Menu;
using Sandbox.Modals;
using Sandbox.Tasks;
using Sentry;
using System;
using System.Reflection;

namespace Sandbox;

/// <summary>
/// Holds the state of a game menu
/// </summary>
internal class GameInstance : IGameInstance
{
	public string Ident { get; set; }

	/// <summary>
	/// Whether we're joining as a client to a host that is running in an editor session.
	/// </summary>
	protected bool IsDeveloperHost => flags.Contains( GameLoadingFlags.Developer );

	public bool WantsToQuit { get; private set; }

	bool _loadingFinished = false;

	public void OnLoadingFinished()
	{
		_loadingFinished = true;
	}

	public bool IsLoading => !_loadingFinished;

	/// <summary>
	/// Access to the filesystem for this game. This is either going to be inside the package or the project folder.
	/// </summary>
	public BaseFileSystem GameFileSystem => activePackage?.FileSystem;

	/// <summary>
	/// Returns true if this game is from a downloaded package, rather than something running locally
	/// </summary>
	public bool IsRemote => activePackage?.Package?.IsRemote ?? false;

	protected PackageManager.ActivePackage activePackage;
	protected Package _package;
	protected Package _mapPackage;

	/// <summary>
	/// Assembly that will be used set during <see cref="LoadAsync"/>.
	/// </summary>
	protected Assembly _packageAssembly;

	public Package Package => _package;

	protected GameLoadingFlags flags;

	public GameInstance( string ident, GameLoadingFlags flags )
	{
		Ident = ident;
		this.flags = flags;
	}

	/// <summary>
	/// Delete this menu, remove all traces of it
	/// </summary>
	public void Close()
	{
		// Close all modals when closing the game menu
		if ( IModalSystem.Current is not null )
		{
			using ( IMenuDll.Current?.PushScope() )
			{
				IModalSystem.Current.CloseAll();
			}
		}

		// Show review modal
		if ( Application.GamePackage is not null )
		{
			using ( IMenuDll.Current?.PushScope() )
			{
				IMenuSystem.Current?.OnPackageClosed( Application.GamePackage );
			}
		}

		// Reset the cursor
		InputRouter.ShutdownUserCursors();
		Mouse.Visibility = MouseVisibility.Auto;

		WantsToQuit = true;
	}

	/// <summary>
	/// Delete/destroy this instance. Unlike Close, this is not called from
	/// the addon gamemenu.. so we don't call gamemenu.closed (which is used
	/// to trigger a navigate to main menu.
	/// </summary>
	internal void Shutdown()
	{
		SentrySdk.AddBreadcrumb( $"Shutdown Game {Ident}", "gameinstance.shutdown" );

		DestroyCustomLoadingScreen();

		if ( _packageAssembly != null )
		{
			ExpirableSynchronizationContext.ForbidPersistentTaskMethods( _packageAssembly );
		}

		_packageAssembly = null;

		// Tear down the active scene while content is still mounted so native handles remain valid during cleanup
		Game.Shutdown();

		GlobalContext.Current.UISystem.Clear();

		if ( activePackage != null && !Application.IsStandalone )
		{
			Game.Language?.Shutdown();
			FileSystem.Mounted?.UnMount( activePackage.FileSystem );

			activePackage = null;
		}

		PackageManager.UnmountTagged( "gamemenu" );

		// Is this the right place for it? Map packages are marked with "game" so they never get unmounted
		PackageManager.UnmountTagged( "game" );

		GameInstanceDll.Current.Shutdown( this );

		// If we were running a benchmark, leave the game
		if ( Application.IsBenchmark )
		{
			if ( !Bootstrap.TryLoadNextBenchmarkPackage() )
			{
				Console.WriteLine( "Quitting" );
				ConVarSystem.Run( "quit" );
			}
		}

		ErrorReporter.ResetCounters();
	}

	/// <summary>
	/// Try to find and create a custom loading screen from the loading assembly.
	/// Looks for Panel subclasses in any loaded .loading assembly.
	/// The panel will be hosted by the menu's LoadingOverlay rather than a standalone RootPanel.
	/// </summary>
	internal void TryCreateCustomLoadingScreen()
	{
		// Delegates to GameInstanceDll which has the full implementation.
		// GameInstanceDll.TryCreateCustomLoadingScreen() will call back to
		// LoadCustomLoadingScreenStyles() if gameInstance is available.
		GameInstanceDll.Current?.TryCreateCustomLoadingScreen();
	}

	/// <summary>
	/// Load SCSS stylesheets from the Loading folder onto the custom panel.
	/// The panel's auto-loading uses ClassFileLocationAttribute paths relative to the
	/// Loading compiler root, which don't resolve via the mounted filesystem.
	/// For local packages, reads directly from the project's Loading directory on disk.
	/// For remote packages, tries FileSystem.Mounted (network files include .scss).
	/// </summary>
	internal void LoadCustomLoadingScreenStyles( Sandbox.UI.Panel panel )
	{
		// For local packages, the Loading/ folder is at the project root level,
		// NOT inside Code/ or Assets/ (which is what activePackage.FileSystem mounts).
		// Read directly from disk using the project's Loading path.
		if ( activePackage?.Package is LocalPackage localPackage )
		{
			var loadingPath = localPackage.Project.GetLoadingPath();
			if ( System.IO.Directory.Exists( loadingPath ) )
			{
				try
				{
					foreach ( var scssFile in System.IO.Directory.EnumerateFiles( loadingPath, "*.scss", System.IO.SearchOption.AllDirectories ) )
					{
						var scss = System.IO.File.ReadAllText( scssFile );
						if ( !string.IsNullOrEmpty( scss ) )
						{
							panel.StyleSheet.Parse( scss );
						}
					}

					return;
				}
				catch ( Exception e )
				{
					Log.Warning( $"[LoadingScreen] Failed to load stylesheets from disk: {e.Message}" );
				}
			}
		}

		// For remote packages, SCSS files are sent via network tables and
		// mounted to FileSystem.Mounted. Try that as a fallback.
		try
		{
			foreach ( var file in FileSystem.Mounted.FindFile( "Loading", "*.scss", true ) )
			{
				var fullPath = $"Loading/{file}";
				var scss = FileSystem.Mounted.ReadAllText( fullPath );

				if ( !string.IsNullOrEmpty( scss ) )
				{
					panel.StyleSheet.Parse( scss );
				}
			}
		}
		catch { }
	}

	/// <summary>
	/// Destroy the custom loading screen panel if one was created.
	/// </summary>
	private static void DestroyCustomLoadingScreen()
	{
		if ( LoadingScreen.CustomPanel is not null )
		{
			LoadingScreen.CustomPanel.Delete( true );
			LoadingScreen.CustomPanel = null;
		}
	}

	public InputSettings InputSettings => ProjectSettings.Input;

	/// <summary>
	/// Attempt to download this package and mount it as a game menu
	/// </summary>
	public virtual async Task<bool> LoadAsync( PackageLoader.Enroller enroller, CancellationToken token )
	{
		Log.Trace( $"LoadAsync: {Ident} (dev:{IsDeveloperHost})" );
		SentrySdk.AddBreadcrumb( $"Loading Game {Ident}", "gameinstance.load" );

		_package = await Package.FetchAsync( Ident, false );

		if ( !IsDeveloperHost )
		{
			if ( Package is null )
			{
				Log.Warning( $"Package {Ident} wasn't found!" );
				return false;
			}
		}

		Application.GameIdent = Package is null ? Ident : $"{_package.Org.Ident}.{_package.Ident}";
		Application.GamePackage = _package;
		Application.ExceptionCount = default;

		//
		// When joining a server, we don't mind if the package is missing or bullshit
		// because they might have some assemblies that run the game.
		//
		if ( Package is null && IsDeveloperHost )
		{
			EngineFileSystem.ProjectSettings = new AggregateFileSystem();
			LoadProjectSettings();
			SetupFileWatch();
			return true;
		}

		var achievementTask = _package.GetAchievements();

		Log.Trace( $"Install Async {Package.Title}" );
		LoadingScreen.Title = $"Installing {Package.Title}";

		var identWithVersion = Package.FullIdent;

		// Make sure we install the correct revision - if we're joining a server
		// we want to download their revision, not the latest one available.
		// Package.FullIdent does not include the version, and in almost all
		// cases except this one, we don't want it to either.
		if ( Package.Revision is not null )
			identWithVersion = $"{identWithVersion}#{Package.Revision.VersionId}";

		using var loadingScreen = new MenuLoadingScreen();

		var downloadOptions = new PackageLoadOptions()
		{
			PackageIdent = identWithVersion,
			ContextTag = "gamemenu",
			CancellationToken = token,
			AllowLocalPackages = !_package.IsRemote,
			Loading = loadingScreen
		};

		activePackage = await PackageManager.InstallAsync( downloadOptions );

		if ( activePackage is null )
		{
			Log.Warning( $"Package {identWithVersion} was null" );
			return false;
		}

		if ( token.IsCancellationRequested )
			return false;

		Log.Trace( $"Loading package {Package.Title}" );
		LoadingScreen.Title = $"Loading {Package.Title}";
		await Task.Delay( 5, token ); // make frame

		try
		{
			// Load the package. Mount it and add it to the file system. Only load the assemblies
			// inside the package if we're not a developer host. If we're a developer host, we
			// load assemblies from the network table.
			if ( !enroller.LoadPackage( Package.FullIdent, !IsDeveloperHost ) )
			{
				if ( IsDeveloperHost )
					return true;

				Log.Warning( "There were errors when trying to load the package" );
				return false;
			}
		}
		catch ( Exception e )
		{
			Log.Warning( e, $"Exception when loading {Package.FullIdent}: {e.Message}" );
			return false;
		}

		TryCreateCustomLoadingScreen();

		//
		// If we have a map package argument - then use it
		//
		if ( !string.IsNullOrWhiteSpace( LaunchArguments.Map ) )
		{
			var map = LaunchArguments.Map;
			await LoadMapPackage( map, token );
			Application.MapPackage = _mapPackage;
		}

		if ( LaunchArguments.GameSettings is not null )
		{
			foreach ( var cvar in LaunchArguments.GameSettings )
			{
				ConVarSystem.SetValue( cvar.Key, cvar.Value, true );
			}
		}

		LoadingScreen.Title = $"Loading Resources";
		await Task.Delay( 5, token ); // make frame

		Log.Trace( $"All Loaded" );

		FileSystem.Mounted.Mount( activePackage.FileSystem );

		EngineFileSystem.ProjectSettings = new AggregateFileSystem();
		EngineFileSystem.ProjectSettings.Mount( activePackage.ProjectSettings );

		Game.Language = new LanguageContainer( activePackage.Localization );

		LoadProjectSettings();

		// If the host is a developer session, we don't want to load all game resources yet. This will be
		// done via the network tables.
		if ( !IsDeveloperHost )
		{
			Log.Trace( $"Loading GameResources" );
			ResourceLoader.LoadAllGameResource( FileSystem.Mounted );
		}

		if ( !achievementTask.IsCompleted )
		{
			LoadingScreen.Title = $"Loading Achievements";
			await achievementTask;
		}

		LoadingScreen.Title = $"Loading Fonts";
		await Task.Delay( 5, token ); // make frame

		Log.Trace( $"Loading Fonts" );
		FontManager.Instance.LoadAll( FileSystem.Mounted );

		SetupFileWatch();

		try
		{
			GameInstanceDll.Current.UpdateProjectConfig( Package );
		}
		catch ( Exception e )
		{
			Log.Warning( e, $"Exception when loading {Package.FullIdent}: {e.Message}" );
			return false;
		}

		_packageAssembly = null;

		if ( Package.TryParseIdent( Package.FullIdent, out var ident ) )
		{
			var loaded = enroller.FindAssembly( Package, $"package.{ident.org}.{ident.package}" );
			if ( loaded is not null )
			{
				_packageAssembly = loaded.Assembly;
			}
		}

		Services.Achievements.DelayAchievementUnlocks( 5 ); // don't trigger achievements for 5 seconds

		//await LoadPackageRevisionAsync( Package.Revision );
		return true;
	}

	private async Task<bool> LoadMapPackage( string map, CancellationToken token )
	{
		if ( _mapPackage is not null )
		{
			// Unload? Maybe?
			_mapPackage = default;
		}

		_mapPackage = await Package.FetchAsync( map, false );

		if ( _mapPackage is null ) return false;
		if ( _mapPackage.TypeName != "map" ) return false;

		// Download map, mount it
		await _mapPackage.MountAsync();

		return true;
	}

	protected void LoadProjectSettings()
	{
		ProjectSettings.ClearCache();

		Input.ReadConfig( ProjectSettings.Input );
		Audio.Mixer.LoadFromSettings( ProjectSettings.Mixer, GlobalContext.Current.TypeLibrary );
		LoadCursors();
	}

	/// <summary>
	/// Load or reload the <see cref="CursorSettings"/> from the active project settings.
	/// </summary>
	internal static void LoadCursors()
	{
		if ( Application.IsHeadless ) return;

		InputRouter.ShutdownUserCursors();

		if ( ProjectSettings.Cursor?.Cursors is null )
			return;

		foreach ( var kv in ProjectSettings.Cursor.Cursors )
		{
			var cursor = kv.Value;
			InputRouter.CreateUserCursor( EngineFileSystem.Mounted, kv.Key, cursor.Image, (int)cursor.Hotspot.x, (int)cursor.Hotspot.y );
		}
	}

	protected void SetupFileWatch()
	{
		var watcher = FileSystem.Mounted.Watch();
		watcher.OnChanges += x =>
		{
			foreach ( var file in x.Changes )
			{
				Texture.Hotload( FileSystem.Mounted, file );
			}
		};

		var settingsWatcher = EngineFileSystem.ProjectSettings.Watch();
		settingsWatcher.OnChanges += x =>
		{
			LoadProjectSettings();
		};
	}

	public void ResetBinds()
	{
		var collection = InputBinds.FindCollection( Application.GameIdent );
		collection.ResetToDefaults();

		SaveBinds();
	}

	public void SaveBinds()
	{
		var collection = InputBinds.FindCollection( Application.GameIdent );
		collection.SaveToDisk();
	}

	public string GetBind( string actionName, out bool isDefault, out bool isCommon )
	{
		var commonBind = InputBinds.FindCollection( "common" ).GetBind( actionName, false );
		var commonValue = InputBinds.FindCollection( "common" ).Get( actionName, 0 );

		var collection = InputBinds.FindCollection( Application.GameIdent );

		isCommon = false;

		var bind = collection.GetBind( actionName );
		var value = collection.Get( actionName, 0 );


		if ( commonBind != null && (string.IsNullOrEmpty( value ) || value == commonValue) )
		{
			isCommon = true;
			value = commonValue;
		}
		else if ( string.IsNullOrWhiteSpace( value ) )
		{
			value = bind.Default;
		}

		isDefault = bind.Default == value;

		return value;
	}

	public void SetBind( string actionName, string buttonName )
	{
		var collection = InputBinds.FindCollection( Application.GameIdent );
		collection.Set( actionName, 0, buttonName );
	}

	/// <summary>
	/// For binding reasons, get a list of buttons that are currently pressed
	/// </summary>
	public void TrapButtons( Action<string[]> callback )
	{
		Game.InputContext.StartTrapping( callback );
	}

	public Scene Scene => Game.ActiveScene;

	public bool OpenStartupScene()
	{
		if ( Game.ActiveScene is not null )
		{
			Game.ActiveScene?.Destroy();
			Game.ActiveScene = null;
		}

		Game.ActiveScene = new Scene();

		if ( IsDeveloperHost )
		{
			// Create a temporary camera so the loading overlay can render
			// via engine overlays. The actual scene content arrives via networking.
			using ( Game.ActiveScene.Push() )
			{
				var go = Game.ActiveScene.CreateObject();
				go.Name = "Loading Camera";
				go.Flags = GameObjectFlags.Hidden | GameObjectFlags.NotSaved;
				var cam = go.AddComponent<CameraComponent>();
				cam.BackgroundColor = Color.Black;
				cam.IsMainCamera = true;
			}

			return true;
		}

		if ( Application.IsEditor && !Game.IsPlaying )
			return true;

		var startup_dedicated = Package.GetMeta<string>( "DedicatedServerStartupScene", "" );
		var startup_game = Package.GetMeta<string>( "StartupScene", "start.scene" );
		var startup_map = Package.GetMeta<string>( "MapStartupScene", null );

		// Get the map launch scene
		var startupScene = startup_game;

		if ( Application.IsDedicatedServer && !string.IsNullOrWhiteSpace( startup_dedicated ) )
		{
			startupScene = startup_dedicated;
		}

		//
		// This is the latest way to do this. We just load this scene directly
		// If it's a vpk, we create a BLANK scene with a loader.
		//
		if ( _mapPackage is not null )
		{
			startupScene = _mapPackage.PrimaryAsset;

			// if it's a VPK, use MapStartupScene for now. Fallback.
			if ( startupScene is null || !startupScene.EndsWith( ".scene" ) )
			{
				startupScene = startup_map ?? startup_game;
			}
		}

		if ( string.IsNullOrWhiteSpace( startupScene ) )
		{
			Log.Warning( "Startup Scene was not defined - can't start!" );
			return false;
		}
		else
		{
			var options = new SceneLoadOptions();

			// this is a blank scene, so it shouldn't matter. We set
			// additive here though, because the ISceneStartup events
			// might be used to load other scenes into it or something.
			options.IsAdditive = true;

			if ( !options.SetScene( startupScene ) )
				return false;

			Game.ActiveScene.RunEvent<ISceneStartup>( x => x.OnHostPreInitialize( options.GetSceneFile() ) );

			if ( !Game.ActiveScene.Load( options ) )
				return false;

			// Since this was an additive load, we need to manually add the system scene
			Game.ActiveScene.AddSystemScene();

			Game.ActiveScene.RunEvent<ISceneStartup>( x => x.OnHostInitialize() );

			if ( !Application.IsDedicatedServer )
			{
				Game.ActiveScene.RunEvent<ISceneStartup>( x => x.OnClientInitialize() );
			}

			return true;
		}
	}
}

class MenuLoadingScreen : ILoadingInterface
{
	public void Dispose()
	{
		LoadingScreen.Title = "";
		LoadingScreen.Subtitle = "";
	}

	public void LoadingProgress( LoadingProgress progress )
	{
		LoadingScreen.Title = $"{progress.Title}";
		LoadingScreen.Subtitle = $"{progress.Percent:n0}% • {progress.Mbps:n0}mbps • {progress.CalculateETA().ToRemainingTimeString()}";
	}
}
