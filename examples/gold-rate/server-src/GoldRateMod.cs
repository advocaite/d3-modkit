using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using DiIiS_NA.GameServer.CommandManager;
using DiIiS_NA.LoginServer.AccountsSystem;
using DiIiS_NA.LoginServer.Battle;
using DiIiS_NA.Mods;

namespace GoldRateMod;

/// <summary>
/// ModKit example, server part. The server finds this class because it implements IServerMod, creates it once at startup and
/// calls Initialize - before the database and the game servers start, so only register things here.
/// </summary>
public sealed class GoldRateServerMod : IServerMod
{
	/// <summary>The current rate; the command below changes it while the server runs.</summary>
	internal static double Multiplier = 1.0;

	public void Initialize(ServerModContext context)
	{
		// settings.json lives in the mod's installed folder (<server>\Mods\example-gold-rate\settings.json)
		var path = Path.Combine(context.Directory, "settings.json");
		if (File.Exists(path))
		{
			using var doc = JsonDocument.Parse(File.ReadAllText(path));
			if (doc.RootElement.TryGetProperty("multiplier", out var m) && m.TryGetDouble(out var v)) Multiplier = v;
		}
		context.Logger.Info("gold drops x{0}", Multiplier.ToString(CultureInfo.InvariantCulture));

		// the hook: runs for every gold drop, after the server's own rates; change Amount to change the drop
		context.Hooks.OnGoldDrop(e => e.Amount = (int)Math.Round(e.Amount * Multiplier));
	}
}

/// <summary>
/// A command group: classes with [CommandGroup] in a mod assembly are found like the server's own. Typed in game chat
/// (or the server console) as "!goldrate" and "!goldrate set 5".
/// </summary>
[CommandGroup("goldrate", "Example mod: shows or changes the gold drop multiplier.\nUsage: goldrate [set <multiplier>]", Account.UserLevels.User)]
public class GoldRateCommands : CommandGroup
{
	[DefaultCommand]
	public string Show(string[] @params, BattleClient invokerClient)
		=> $"Gold drops are multiplied by {GoldRateServerMod.Multiplier.ToString(CultureInfo.InvariantCulture)}.";

	[Command("set", "Sets the multiplier until the server restarts.\nUsage: goldrate set <multiplier>", Account.UserLevels.GM)]
	public string Set(string[] @params, BattleClient invokerClient)
	{
		if (@params == null || @params.Length < 1 || !double.TryParse(@params[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || v < 0 || v > 1000)
			return "Usage: goldrate set <multiplier between 0 and 1000>";
		GoldRateServerMod.Multiplier = v;
		return $"Gold drops are now multiplied by {v.ToString(CultureInfo.InvariantCulture)}.";
	}
}
