using Echse.Core;

namespace Echse.App;

internal static class DemoCatalog
{
    public static List<Game> Games() =>
    [
        new() { Id = "supertuxkart", Title = "SuperTuxKart", Genre = "Racing", Popularity = 980,
            Tags = ["Arcade", "Multiplayer"], Description = "Bunte Strecken, ungewöhnliche Karts und Rennen gegen Freunde. Ein quelloffenes Arcade-Rennspiel." },
        new() { Id = "0ad", Title = "0 A.D.", Genre = "Strategie", Popularity = 920,
            Tags = ["Aufbau", "Historisch"], Description = "Errichte eine antike Zivilisation und führe sie durch große Echtzeitschlachten." },
        new() { Id = "veloren", Title = "Veloren", Genre = "Rollenspiel", Popularity = 890,
            Tags = ["Open World", "Multiplayer", "Entdecken"], Description = "Erkunde eine prozedurale Voxel-Welt voller Landschaften, Dungeons und gemeinsamer Abenteuer." },
        new() { Id = "openttd", Title = "OpenTTD", Genre = "Simulation", Popularity = 850,
            Tags = ["Aufbau", "Strategie"], Description = "Verbinde Städte und Industrien mit deinem eigenen Transportnetz und baue ein Logistikimperium auf." },
        new() { Id = "xonotic", Title = "Xonotic", Genre = "Action", Popularity = 810,
            Tags = ["Arena", "Multiplayer"], Description = "Schnelle Bewegung und präzises Zielen in einem quelloffenen Arena-Shooter." },
        new() { Id = "wesnoth", Title = "The Battle for Wesnoth", Genre = "Strategie", Popularity = 780,
            Tags = ["Rundenbasiert", "Fantasy"], Description = "Taktische Gefechte auf Hexfeldern, lange Kampagnen und eine vielseitige Fantasy-Welt." },
        new() { Id = "mindustry", Title = "Mindustry", Genre = "Strategie", Popularity = 760,
            Tags = ["Aufbau", "Automation"], Description = "Verbinde Produktionsketten mit Tower-Defense und verteidige deine Versorgungslinien." },
        new() { Id = "luanti", Title = "Luanti", Genre = "Sandbox", Popularity = 730,
            Tags = ["Open World", "Aufbau", "Entdecken"], Description = "Eine offene Plattform für Voxel-Welten, kreative Bauprojekte und gemeinsames Spielen." }
    ];
}
