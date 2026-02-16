using System.IO;
using Sandbox;
using Sandbox.UI;

namespace Editor;

/// <summary>
/// Editor dock tab that renders a live preview of the custom loading screen.
/// Uses an editor scene with a camera and ScreenPanel to render the loading UI.
/// ScreenPanel implements ExecuteInEditor so its lifecycle works in editor scenes.
/// </summary>
[Dock( "Editor", "Loading Screen", "hourglass_empty" )]
public class LoadingScreenPreviewDock : Widget
{
	SceneRenderingWidget _renderer;
	Scene _previewScene;
	CameraComponent _camera;
	ScreenPanel _screenPanel;
	Panel _loadingPanel;
	bool _isActive;

	// Toolbar widgets
	Widget _toolbar;
	Label _statusLabel;

	// Saved loading screen state
	string _savedTitle;
	string _savedSubtitle;
	bool _savedIsReadyToJoin;
	List<LoadingContext> _savedTasks;

	// Mock loading simulation
	float _mockTime;
	const float MockLoadDuration = 17f;
	const float MockReadyDuration = 4f;
	const float MockCycleDuration = MockLoadDuration + MockReadyDuration;

	public LoadingScreenPreviewDock( Widget parent ) : base( parent )
	{
		Layout = Layout.Column();
		DeleteOnClose = true;

		BuildToolbar();
		CreatePreview();
	}

	void BuildToolbar()
	{
		_toolbar = new Widget( this );
		_toolbar.FixedHeight = Theme.ControlHeight + 12;
		_toolbar.OnPaintOverride = () =>
		{
			Paint.ClearPen();
			Paint.SetBrush( Theme.SurfaceBackground );
			Paint.DrawRect( _toolbar.LocalRect );
			return true;
		};

		var row = _toolbar.Layout = Layout.Row();
		row.Margin = new Sandbox.UI.Margin( 8, 6, 8, 6 );
		row.Spacing = 6;

		// Icon + title
		var titleLabel = row.Add( new Label( "Loading Screen Preview" ) );
		titleLabel.SetStyles( $"font-weight: 600; color: {Theme.Text.Hex};" );

		row.AddStretchCell();

		// Status label (right side)
		_statusLabel = row.Add( new Label() );
		_statusLabel.SetStyles( $"color: {Theme.TextLight.Hex};" );

		row.AddSpacingCell( 4 );

		// Refresh button
		var refreshBtn = row.Add( new IconButton( "refresh" ) );
		refreshBtn.ToolTip = "Rebuild Preview";
		refreshBtn.OnClick = () => CreatePreview();

		Layout.Add( _toolbar );
	}

	void CreatePreview()
	{
		DestroyPreview();

		var panelType = FindLoadingPanelType();
		if ( panelType is null )
		{
			ShowStatusMessage( GetStatusText() );
			return;
		}

		// Remove any existing status message
		RemoveStatusMessage();

		// Save current loading screen state
		_savedTitle = LoadingScreen.Title;
		_savedSubtitle = LoadingScreen.Subtitle;
		_savedIsReadyToJoin = LoadingScreen.IsReadyToJoin;
		_savedTasks = new List<LoadingContext>( LoadingScreen.Tasks );

		// Set initial mock data
		_mockTime = 0;
		ApplyMockState();

		// Use an editor scene. ScreenPanel now implements ExecuteInEditor,
		// so its OnAwake fires and creates the root panel.
		_previewScene = Scene.CreateEditorScene();
		using ( _previewScene.Push() )
		{
			// Camera
			var cameraGo = _previewScene.CreateObject();
			cameraGo.Name = "Preview Camera";
			_camera = cameraGo.AddComponent<CameraComponent>();
			_camera.BackgroundColor = Color.Black;
			_camera.IsMainCamera = true;

			// ScreenPanel for UI rendering
			var screenPanelGo = _previewScene.CreateObject();
			screenPanelGo.Name = "Loading Screen";
			_screenPanel = screenPanelGo.AddComponent<ScreenPanel>();
			_screenPanel.ZIndex = 100;

			// OnAwake fires during AddComponent (via CallbackBatch) since
			// ScreenPanel now has ExecuteInEditor. GetPanel() should be valid.
			var rootPanel = _screenPanel.GetPanel();
			if ( rootPanel is null )
			{
				Log.Warning( "[LoadingPreview] ScreenPanel.GetPanel() returned null after AddComponent" );
				_previewScene.Destroy();
				_previewScene = null;
				ShowStatusMessage( "Failed to initialize ScreenPanel." );
				return;
			}

			try
			{
				_loadingPanel = panelType.Create<Panel>();
				LoadProjectStyleSheets( _loadingPanel );
				rootPanel.AddChild( _loadingPanel );
				_isActive = true;
			}
			catch ( System.Exception e )
			{
				Log.Warning( $"[LoadingPreview] Failed to create loading screen panel: {e.Message}" );
				_previewScene.Destroy();
				_previewScene = null;
				ShowStatusMessage( $"Failed to create panel:\n{e.Message}" );
				return;
			}
		}

		// Create the renderer widget
		_renderer = new SceneRenderingWidget( this );
		_renderer.Scene = _previewScene;
		_renderer.Camera = _camera;
		Layout.Add( _renderer, 1 );

		UpdateStatusLabel();
	}

	string GetMockGameTitle()
	{
		return Project.Current?.Config?.Title ?? "My Game";
	}

	/// <summary>
	/// Updates LoadingScreen state based on the current mock time.
	/// Simulates a realistic loading sequence that loops.
	/// </summary>
	void ApplyMockState()
	{
		var gameTitle = GetMockGameTitle();
		var t = _mockTime;

		// Phase 1 (0-2s): Installing - initial download
		if ( t < 2f )
		{
			LoadingScreen.IsReadyToJoin = false;
			LoadingScreen.Title = $"Installing {gameTitle}";
			LoadingScreen.Subtitle = "Downloading resources...";
			LoadingScreen.Tasks.Clear();
		}
		// Phase 2 (2-6s): Installing - download progress with percentage
		else if ( t < 6f )
		{
			var progress = (t - 2f) / 4f;
			var percent = progress * 100f;
			var mbps = 42f + MathF.Sin( t * 3f ) * 12f;
			var remaining = (int)((1f - progress) * 8f) + 1;

			LoadingScreen.IsReadyToJoin = false;
			LoadingScreen.Title = $"Installing {gameTitle}";
			LoadingScreen.Subtitle = $"{percent:n0}% \u00b7 {mbps:n0} mbps \u00b7 {remaining}s remaining";
			LoadingScreen.Tasks.Clear();
		}
		// Phase 3 (6-8s): Loading package assemblies
		else if ( t < 8f )
		{
			LoadingScreen.IsReadyToJoin = false;
			LoadingScreen.Title = $"Loading {gameTitle}";
			LoadingScreen.Subtitle = "Initializing assemblies...";
			LoadingScreen.Tasks.Clear();
		}
		// Phase 4 (8-11s): Loading resources with multiple tasks
		else if ( t < 11f )
		{
			LoadingScreen.IsReadyToJoin = false;
			LoadingScreen.Title = "Loading Resources";
			LoadingScreen.Subtitle = "";
			LoadingScreen.Tasks.Clear();

			if ( t < 9.5f )
			{
				LoadingScreen.Tasks.Add( new LoadingContext { Title = "Loading Models" } );
				LoadingScreen.Tasks.Add( new LoadingContext { Title = "Loading Textures" } );
				LoadingScreen.Tasks.Add( new LoadingContext { Title = "Loading Sounds" } );
			}
			else
			{
				LoadingScreen.Tasks.Add( new LoadingContext { Title = "Loading Textures" } );
				LoadingScreen.Tasks.Add( new LoadingContext { Title = "Loading Sounds" } );
			}
		}
		// Phase 5 (11-13s): Loading resources - fewer tasks remaining
		else if ( t < 13f )
		{
			LoadingScreen.IsReadyToJoin = false;
			LoadingScreen.Title = "Loading Resources";
			LoadingScreen.Subtitle = "";
			LoadingScreen.Tasks.Clear();
			LoadingScreen.Tasks.Add( new LoadingContext { Title = "Loading Sounds" } );
		}
		// Phase 6 (13-15s): Loading scene with task
		else if ( t < 15f )
		{
			LoadingScreen.IsReadyToJoin = false;
			LoadingScreen.Title = "Loading Scene";
			LoadingScreen.Subtitle = "";
			LoadingScreen.Tasks.Clear();
			LoadingScreen.Tasks.Add( new LoadingContext { Title = "Spawning Entities" } );
		}
		// Phase 7 (15-17s): Generating NavMesh
		else if ( t < MockLoadDuration )
		{
			LoadingScreen.IsReadyToJoin = false;
			LoadingScreen.Title = "Loading Scene";
			LoadingScreen.Subtitle = "Generating NavMesh..";
			LoadingScreen.Tasks.Clear();
		}
		// Phase 8 (17-21s): Ready to join
		else
		{
			LoadingScreen.IsReadyToJoin = true;
			LoadingScreen.Title = "Ready";
			LoadingScreen.Subtitle = "";
			LoadingScreen.Tasks.Clear();
		}
	}

	void DestroyPreview()
	{
		_isActive = false;
		_loadingPanel = null;
		_screenPanel = null;
		_camera = null;

		if ( _renderer.IsValid() )
		{
			_renderer.Destroy();
			_renderer = null;
		}

		if ( _previewScene is not null )
		{
			_previewScene.Destroy();
			_previewScene = null;

			// Restore saved loading screen state
			LoadingScreen.Title = _savedTitle;
			LoadingScreen.Subtitle = _savedSubtitle;
			LoadingScreen.IsReadyToJoin = _savedIsReadyToJoin;
			LoadingScreen.Tasks.Clear();
			if ( _savedTasks is not null )
			{
				LoadingScreen.Tasks.AddRange( _savedTasks );
				_savedTasks = null;
			}
		}

		RemoveStatusMessage();
	}

	Widget _statusWidget;

	void ShowStatusMessage( string text )
	{
		RemoveStatusMessage();

		_statusWidget = new Widget( this );
		_statusWidget.Layout = Layout.Column();
		_statusWidget.Layout.Alignment = TextFlag.Center;
		_statusWidget.Layout.AddStretchCell();

		var icon = _statusWidget.Layout.Add( new Label( "" ) );
		icon.SetStyles( "font-size: 48px; color: rgba(255,255,255,0.15);" );

		_statusWidget.Layout.AddSpacingCell( 8 );

		var label = _statusWidget.Layout.Add( new Label( text ) );
		label.WordWrap = true;
		label.Alignment = TextFlag.Center;
		label.SetStyles( "color: rgba(255,255,255,0.4); padding: 16px;" );

		_statusWidget.Layout.AddStretchCell();

		Layout.Add( _statusWidget, 1 );

		if ( _statusLabel.IsValid() )
			_statusLabel.Text = "No loading screen";
	}

	void RemoveStatusMessage()
	{
		if ( _statusWidget.IsValid() )
		{
			_statusWidget.Destroy();
			_statusWidget = null;
		}
	}

	void UpdateStatusLabel()
	{
		if ( !_statusLabel.IsValid() )
			return;

		if ( _isActive )
		{
			var panelType = FindLoadingPanelType();
			_statusLabel.Text = panelType?.Name ?? "";
		}
		else
		{
			_statusLabel.Text = "No loading screen";
		}
	}

	string GetStatusText()
	{
		if ( Project.Current is null )
			return "No project loaded.";

		if ( !Project.Current.HasLoadingPath() )
			return "No Loading folder found.\n\nCreate a Loading/ folder in your project root with a Razor panel to get started.";

		return "Loading folder exists but no Panel subclass was found.\n\nAdd a Razor file that inherits Panel.";
	}

	/// <summary>
	/// Manually loads SCSS files from the loading project directory onto the panel.
	/// The auto-loading mechanism uses ClassFileLocationAttribute paths that are relative
	/// to the loading compiler root, which don't resolve via the editor's FileMount.
	/// </summary>
	void LoadProjectStyleSheets( Panel panel )
	{
		if ( Project.Current is null || !Project.Current.HasLoadingPath() )
			return;

		var loadingPath = Project.Current.GetLoadingPath();

		foreach ( var scssFile in Directory.EnumerateFiles( loadingPath, "*.scss", SearchOption.AllDirectories ) )
		{
			try
			{
				var scss = File.ReadAllText( scssFile );
				panel.StyleSheet.Parse( scss );
			}
			catch ( System.Exception e )
			{
				Log.Warning( $"[LoadingPreview] Failed to load stylesheet {scssFile}: {e.Message}" );
			}
		}
	}

	TypeDescription FindLoadingPanelType()
	{
		return Game.TypeLibrary?.GetTypes<Panel>()
			.Where( x => x.TargetType.Assembly.GetName().Name?.EndsWith( ".loading" ) == true )
			.FirstOrDefault();
	}

	[EditorEvent.Frame]
	public void Frame()
	{
		if ( !Visible )
			return;

		// Tick the preview scene to keep UI alive (animations, transitions, BuildHash)
		if ( _isActive && _previewScene.IsValid() )
		{
			// Advance mock loading simulation
			_mockTime += RealTime.Delta;
			if ( _mockTime >= MockCycleDuration )
				_mockTime = 0;

			ApplyMockState();

			using ( _previewScene.Push() )
			{
				_previewScene.EditorTick( RealTime.Now, RealTime.Delta );
			}
		}

		// Auto-detect when loading panel appears or disappears
		var hasPanel = FindLoadingPanelType() is not null;
		if ( hasPanel != _isActive )
		{
			CreatePreview();
		}
	}

	[EditorEvent.Hotload]
	public void OnHotload()
	{
		CreatePreview();
	}

	public override void OnDestroyed()
	{
		DestroyPreview();
		base.OnDestroyed();
	}
}
