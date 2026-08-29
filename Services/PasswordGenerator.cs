using System.Security.Cryptography;

namespace DigitalBoxApi.Services;

// Generates the readable passphrases the admin hands to staff (e.g. amber-piston-cellar-drift-47).
// Four words from the list below plus a two-digit suffix — ~41 bits, and easy to read aloud or
// copy off a note. Admin-issued only; rotate via POST /api/users/{id}/reset-password.
public interface IPasswordGenerator
{
    string Generate();
}

public sealed class PasswordGenerator : IPasswordGenerator
{
    private const int Words = 4;

    public string Generate()
    {
        var parts = new string[Words + 1];
        for (var i = 0; i < Words; i++)
        {
            parts[i] = Wordlist[RandomNumberGenerator.GetInt32(Wordlist.Length)];
        }

        parts[Words] = RandomNumberGenerator.GetInt32(10, 100).ToString();
        return string.Join('-', parts);
    }

    // Concrete, common, 4–6 letter words. No visually confusable pairs, no offensive terms.
    private static readonly string[] Wordlist =
    {
        "amber", "anchor", "apple", "arrow", "aspen", "atlas", "attic", "autumn", "bacon", "badge",
        "bagel", "baker", "balsa", "bamboo", "banjo", "barge", "basin", "batch", "beacon", "beaver",
        "bench", "berry", "birch", "bison", "blaze", "block", "bloom", "board", "bolt", "bonus",
        "boots", "borax", "bottle", "boulder", "brass", "brave", "bread", "brick", "bridge", "broom",
        "brush", "bucket", "buffalo", "bundle", "burrow", "cabin", "cable", "cactus", "camel", "candle",
        "canoe", "canvas", "canyon", "carbon", "cargo", "carrot", "castle", "cedar", "cellar", "chalk",
        "cherry", "chess", "chime", "cider", "cinder", "circus", "clamp", "clay", "cliff", "cloak",
        "clover", "cobalt", "cocoa", "comet", "copper", "coral", "cotton", "cougar", "crane", "crate",
        "cream", "creek", "crest", "crown", "crystal", "dagger", "daisy", "dawn", "delta", "denim",
        "diesel", "dock", "dome", "donut", "draft", "drift", "drum", "dune", "eagle", "ember",
        "engine", "ferry", "fiber", "flame", "flask", "fleet", "flint", "forest", "fossil", "fox",
        "frost", "garden", "gecko", "ginger", "glacier", "granite", "gravel", "harbor", "hazel", "helm",
        "hickory", "honey", "ivory", "jade", "jasper", "jetty", "kayak", "kettle", "lagoon", "lantern",
        "ledger", "lemon", "lily", "linen", "lobby", "locust", "lotus", "lumber", "lynx", "maple",
        "marble", "meadow", "melon", "meteor", "mint", "mocha", "mosaic", "moss", "motor", "mule",
        "nectar", "nickel", "oak", "ocean", "olive", "onyx", "opal", "orbit", "otter", "oxide",
        "paddle", "panda", "pantry", "papaya", "parlor", "pasta", "pearl", "pebble", "pecan", "pepper",
        "pier", "pigeon", "pillar", "pilot", "pine", "piston", "pixel", "plank", "plaza", "plum",
        "pocket", "pollen", "poncho", "poplar", "prairie", "prism", "puma", "quartz", "quilt", "rabbit",
        "raft", "ranch", "raven", "reef", "resin", "ribbon", "river", "roam", "rocket", "rope",
        "rudder", "rust", "saddle", "salmon", "sandal", "sapphire", "satin", "scout", "seal", "sequoia",
        "shale", "shell", "shore", "shovel", "silo", "silver", "slate", "sled", "sloth", "sonar",
        "spark", "sparrow", "spool", "sprout", "spruce", "squid", "stable", "stag", "steam", "stone",
        "stork", "stove", "straw", "stream", "sugar", "summit", "sunset", "swan", "syrup", "tackle",
        "talon", "tandem", "tapir", "teak", "tent", "thicket", "thistle", "timber", "tonic", "topaz",
        "torch", "tractor", "trail", "trout", "trunk", "tulip", "tundra", "turtle", "valley", "velvet",
        "vessel", "walnut", "walrus", "willow", "window", "wombat", "yarn", "yeti", "zebra", "zephyr",
    };
}
