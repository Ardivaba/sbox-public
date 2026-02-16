# Custom Loading Screens

S&box supports custom Razor UI loading screens that display while your game loads. Players can interact with your loading screen before assets are fully loaded.

## Getting Started

Create a `Loading` folder in your project root, alongside `Code` and `Editor`:

```
MyGame/
  Assets/
  Code/
  Editor/
  Loading/        <-- your loading screen goes here
  .sbproj
```

Add a Razor file inside the `Loading` folder:

```razor
@using Sandbox;
@using Sandbox.UI;
@inherits Panel

<root>
    <div class="loading-container">
        <h1>@LoadingScreen.Title</h1>
        <p>@LoadingScreen.Subtitle</p>

        @if ( LoadingScreen.Tasks.Count > 0 )
        {
            <div class="tasks">
                @foreach ( var task in LoadingScreen.Tasks )
                {
                    <div class="task">@task.Title</div>
                }
            </div>
        }
    </div>
</root>

@code
{
    protected override int BuildHash()
        => HashCode.Combine( LoadingScreen.Title, LoadingScreen.Subtitle, LoadingScreen.Tasks.Count );
}
```

Style it with a `.scss` file in the same folder:

```scss
LoadingContainer {
    width: 100%;
    height: 100%;
    justify-content: center;
    align-items: center;
    background-color: #1a1a2e;
    color: white;
    font-family: Poppins;

    h1 {
        font-size: 48px;
        margin-bottom: 12px;
    }

    .tasks {
        margin-top: 24px;
        font-size: 16px;
        opacity: 0.7;
    }
}
```

## How It Works

The `Loading` folder compiles as a separate assembly that loads early in the startup pipeline, before your game's assets and scene. When the engine detects a `Panel` subclass in the loading assembly, it creates and displays it automatically.

Your custom loading screen replaces the default menu loading overlay. When loading finishes, the panel is removed and your game starts normally.

## Available APIs

Your loading screen panel has access to:

| API | Description |
|---|---|
| `LoadingScreen.IsVisible` | Whether the loading screen is currently shown |
| `LoadingScreen.Title` | Current loading phase title (e.g. "Loading Resources") |
| `LoadingScreen.Subtitle` | Additional detail text (e.g. download progress) |
| `LoadingScreen.Media` | Background image URL if set by the package |
| `LoadingScreen.Tasks` | List of active `LoadingContext` tasks being awaited |
| `FileSystem.Data` | Per-game data folder, shared with your game code |

## Sharing State with Game Code

Use `FileSystem.Data` to persist state between your loading screen and game code. For example, store player preferences during loading that your game reads on startup:

```csharp
// In Loading/MyLoadingScreen.razor
FileSystem.Data.WriteAllText( "loading_preferences.json", json );

// In Code/MyGameSystem.cs
var json = FileSystem.Data.ReadAllText( "loading_preferences.json" );
```

## Constraints

- Loading code compiles independently from your `Code` folder. You cannot reference types defined in `Code` from `Loading`.
- The loading panel must inherit from `Panel` (directly or via Razor `@inherits`).
- Only one loading screen panel is supported per game. If multiple `Panel` subclasses exist in the loading assembly, the first one found is used.
- Loading screens are not shown on dedicated servers or headless instances.
