namespace Sandbox;

/// <summary>
/// Holds metadata and raw data relating to a Saved Game.
/// </summary>
public static class LoadingScreen
{
	private static bool _loading;

	public static bool IsVisible
	{
		get => _loading;
		set
		{
			if ( _loading == value )
				return;

			//Log.Info( $"Loading: {value}\n{new StackTrace( true ).ToString()}" );

			_loading = value;
		}
	}

	/// <summary>
	/// A title to show
	/// </summary>
	public static string Title { get; set; } = "Loading..";

	/// <summary>
	/// A subtitle to show
	/// </summary>
	public static string Subtitle { get; set; } = "";

	/// <summary>
	/// A URL or filepath to show as the background image.
	/// </summary>
	public static string Media { get; set; }

	/// <summary>
	/// A list of tasks that are currently being awaited during loading.
	/// </summary>
	public static List<LoadingContext> Tasks { get; } = [];

	/// <summary>
	/// The active custom loading screen panel from the game's Loading folder, if any.
	/// </summary>
	public static UI.Panel CustomPanel { get; set; }

	/// <summary>
	/// If true, a custom loading screen from the game's Loading folder is active.
	/// The menu's default loading overlay should hide when this is true.
	/// </summary>
	public static bool HasCustomLoadingScreen => CustomPanel is not null;

	/// <summary>
	/// If true, loading has completed and the game is ready to join.
	/// The custom loading screen should show a "Join" button when this is true.
	/// </summary>
	public static bool IsReadyToJoin { get; set; }

	/// <summary>
	/// Called from the custom loading screen's "Join" button.
	/// Dismisses the loading screen and enters the game.
	/// </summary>
	public static void Join()
	{
		IsVisible = false;
		IsReadyToJoin = false;

		if ( CustomPanel is not null )
		{
			CustomPanel.Delete( true );
			CustomPanel = null;
		}
	}

	/// <summary>
	/// Called by the scene system to tell us about the loading tasks
	/// </summary>
	internal static void UpdateLoadingTasks( List<LoadingContext> incoming )
	{
		Tasks.Clear();

		if ( incoming.Count > 0 )
		{
			Tasks.AddRange( incoming );
		}
	}

}
