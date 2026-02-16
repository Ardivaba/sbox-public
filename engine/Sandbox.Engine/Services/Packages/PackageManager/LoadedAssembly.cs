using System.Reflection;

namespace Sandbox;

/// <summary>
/// An assembly that has been loaded into a PackageLoader.
/// </summary>
internal class LoadedAssembly
{
	public Package Package { get; set; }
	public string Name { get; set; }
	public System.Version Version { get; set; }
	public Assembly Assembly { get; set; }
	public byte[] CodeArchiveBytes { get; set; }
	public byte[] CompiledAssemblyBytes { get; set; }
	public bool FastHotload { get; set; }
	public bool FullHotload { get; set; }
	public bool IsFirstVersion => !FastHotload && !FullHotload;

	string typeName => Package?.TypeName ?? "game";

	public bool IsGame => typeName == "game";
	public bool IsLibrary => typeName == "library";
	public bool IsTool => typeName == "tool";

	/// <summary>
	/// If not null, this is an assembly that was created by Fast Hotload
	/// </summary>
	public Assembly ModifiedAssembly { get; set; }

	public bool IsEditorAssembly
	{
		get
		{
			if ( IsTool ) return true;
			if ( IsGame && Name.EndsWith( ".editor" ) ) return true;
			if ( IsLibrary && Name.EndsWith( ".editor" ) ) return true;

			return false;
		}
	}

	public bool IsLoadingAssembly
	{
		get
		{
			if ( IsGame && Name.EndsWith( ".loading" ) ) return true;
			if ( IsLibrary && Name.EndsWith( ".loading" ) ) return true;

			return false;
		}
	}
}
