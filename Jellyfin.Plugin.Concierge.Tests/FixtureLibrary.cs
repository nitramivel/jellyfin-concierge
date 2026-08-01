using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Concierge.Core.Documents;

namespace Jellyfin.Plugin.Concierge.Tests
{
    /// <summary>
    /// A small, deliberately recognisable library to run retrieval against.
    /// </summary>
    /// <remarks>
    /// Real titles with their real overviews, and enrichment of the shape the index
    /// pass actually produces. Recognisable on purpose: an assertion that
    /// "dark and twisted" should rank Se7en above Paddington is one a reader can
    /// check without running anything, and a fixture of Film A / Film B would make
    /// the same test unreadable.
    /// <para>
    /// This is <b>not</b> a substitute for the evaluation set in <c>eval/</c>. That
    /// one runs against the owner's real library with a real embedding model and
    /// measures quality. This one pins mechanics: field weighting, era vocabulary,
    /// row collapsing, and fusion.
    /// </para>
    /// </remarks>
    internal static class FixtureLibrary
    {
        /// <summary>Every fixture document, enrichment attached.</summary>
        public static IReadOnlyList<ItemDocument> All { get; } = Build();

        /// <summary>Looks a fixture up by title.</summary>
        /// <param name="title">The title.</param>
        /// <returns>Its id.</returns>
        public static Guid Id(string title)
            => All.First(d => string.Equals(d.Title, title, StringComparison.OrdinalIgnoreCase)).ItemId;

        /// <summary>Names the items behind a ranked list, for readable assertions.</summary>
        /// <param name="ids">The ranked ids.</param>
        /// <returns>The titles, in the same order.</returns>
        public static IReadOnlyList<string> Titles(IEnumerable<Guid> ids)
        {
            var byId = All.ToDictionary(d => d.ItemId, d => d.Title);
            return ids.Select(id => byId.TryGetValue(id, out var title) ? title : id.ToString()).ToList();
        }

        private static List<ItemDocument> Build()
        {
            var documents = new List<ItemDocument>
            {
                Film(
                    "Se7en", 1995,
                    ["Crime", "Thriller", "Mystery"],
                    ["Brad Pitt", "Morgan Freeman", "Kevin Spacey", "David Fincher"],
                    "Two detectives, a rookie and a veteran, hunt a serial killer who uses the seven deadly sins as his motives.",
                    new Enrichment(
                        "Two detectives track a meticulous killer staging murders around the seven deadly sins, and the case ends by making one of them part of it.",
                        ["the box in the desert", "the sloth victim still alive", "what's in the box"],
                        ["dark", "twisted", "bleak", "grim", "serial killer", "moral decay", "dread", "rain-soaked city"],
                        [
                            "the one with the seven deadly sins killer",
                            "that grim 90s thriller with the box at the end",
                            "dark rainy detective film where the ending is devastating",
                            "the one where the killer turns himself in halfway through",
                        ],
                        Spoiler: true)),

                Film(
                    "The Silence of the Lambs", 1991,
                    ["Crime", "Thriller", "Horror"],
                    ["Jodie Foster", "Anthony Hopkins", "Jonathan Demme"],
                    "A young FBI cadet must confide in an incarcerated and manipulative killer to receive his help on catching another serial killer.",
                    new Enrichment(
                        "A trainee agent bargains with an imprisoned cannibal psychiatrist for insight into a killer who is skinning his victims.",
                        ["the fava beans line", "the basement night-vision sequence", "the mask"],
                        ["dark", "twisted", "tense", "psychological", "predatory", "claustrophobic", "cat and mouse"],
                        [
                            "the one with the cannibal psychiatrist",
                            "that film where she interviews him through the glass",
                            "dark twisted thriller with the moth",
                            "the one with it puts the lotion in the basket",
                        ],
                        Spoiler: false)),

                Film(
                    "Fargo", 1996,
                    ["Crime", "Comedy", "Drama"],
                    ["Frances McDormand", "William H. Macy", "Steve Buscemi", "Joel Coen"],
                    "A car salesman's botched attempt to have his wife kidnapped turns bloody, and a pregnant police chief investigates.",
                    new Enrichment(
                        "A weak man hires two criminals to kidnap his own wife for ransom money, and a calm pregnant sheriff unpicks the killings that follow.",
                        ["the wood chipper", "the snowbound parking lot", "the pregnant sheriff eating lunch"],
                        ["dark", "twisted", "deadpan", "bleak comedy", "snowbound", "small-town", "banal evil", "funny"],
                        [
                            "the one with the wood chipper",
                            "that snowy crime film with the polite accents",
                            "dark comedy where a man has his own wife kidnapped",
                            "90s film with the pregnant police chief",
                        ],
                        Spoiler: false)),

                Film(
                    "Groundhog Day", 1993,
                    ["Comedy", "Romance", "Fantasy"],
                    ["Bill Murray", "Andie MacDowell", "Harold Ramis"],
                    "A cynical TV weatherman finds himself reliving the same day over and over again.",
                    new Enrichment(
                        "A sour weatherman is trapped repeating one small-town day until he stops trying to exploit it and becomes someone worth being.",
                        ["the alarm clock at 6am", "driving off the quarry with the groundhog", "learning piano"],
                        ["funny", "warm", "nostalgic", "comfort watch", "redemption", "time loop", "gentle", "feel-good"],
                        [
                            "the one where he lives the same day over and over",
                            "that comedy with the weatherman and the groundhog",
                            "warm 90s comedy about a man stuck in a time loop",
                            "feel-good film where a rude man slowly becomes kind",
                        ],
                        Spoiler: false)),

                Film(
                    "Jurassic Park", 1993,
                    ["Adventure", "Sci-Fi", "Thriller"],
                    ["Sam Neill", "Laura Dern", "Jeff Goldblum", "Steven Spielberg"],
                    "A pragmatic paleontologist visiting an almost complete theme park is tasked with protecting a couple of kids after a power failure causes the park's cloned dinosaurs to run loose.",
                    new Enrichment(
                        "Scientists tour a park of cloned dinosaurs, the power fails, and the animals stop being an attraction.",
                        ["the ripples in the water glass", "the kitchen raptors", "the first sight of the brachiosaur"],
                        ["nostalgic", "adventure", "wonder", "tense", "blockbuster", "childhood favourite", "spectacle"],
                        [
                            "the one with the dinosaurs in the theme park",
                            "that film with the ripples in the water glass",
                            "nostalgic 90s adventure with raptors in a kitchen",
                            "childhood favourite about cloned dinosaurs",
                        ],
                        Spoiler: false)),

                Film(
                    "Clueless", 1995,
                    ["Comedy", "Romance"],
                    ["Alicia Silverstone", "Paul Rudd", "Brittany Murphy", "Amy Heckerling"],
                    "A rich high school student tries to boost a new pupil's popularity, but reckons without affairs of the heart.",
                    new Enrichment(
                        "A wealthy, well-meaning teenager makes a project of everyone else's love life while missing her own.",
                        ["the computerised wardrobe", "as if", "the freeway driving lesson"],
                        ["funny", "warm", "nostalgic", "sunny", "light", "comfort watch", "teen comedy", "stylish"],
                        [
                            "the one with the computer that picks her outfits",
                            "sunny 90s teen comedy in beverly hills",
                            "nostalgic light comedy where she says as if",
                            "feel-good high school film about matchmaking",
                        ],
                        Spoiler: false)),

                Film(
                    "The Big Lebowski", 1998,
                    ["Comedy", "Crime"],
                    ["Jeff Bridges", "John Goodman", "Julianne Moore", "Joel Coen"],
                    "Jeff 'The Dude' Lebowski, mistaken for a millionaire of the same name, seeks restitution for his ruined rug.",
                    new Enrichment(
                        "A slacker is mistaken for a millionaire and drifts through a kidnapping plot he never understands, mostly wanting his rug replaced.",
                        ["the rug that really tied the room together", "the bowling dream sequence", "the ashes on the cliff"],
                        ["funny", "shaggy", "cult", "nostalgic", "laid-back", "absurd", "comfort watch"],
                        [
                            "the one about the guy whose rug gets ruined",
                            "that bowling comedy with the dude",
                            "shaggy 90s cult comedy about mistaken identity",
                        ],
                        Spoiler: false)),

                Film(
                    "The Truman Show", 1998,
                    ["Drama", "Comedy", "Sci-Fi"],
                    ["Jim Carrey", "Ed Harris", "Laura Linney", "Peter Weir"],
                    "In a picture-perfect seaside town, an insurance salesman begins to realize that his entire existence may be staged and observed by a vast unseen audience as part of a long-running real-time reality TV show.",
                    new Enrichment(
                        "A man discovers the town he has lived in all his life is a television set and everyone in it is an actor.",
                        ["the studio light falling from the sky", "the boat hitting the painted wall", "good morning and in case I don't see you"],
                        ["surreal", "melancholy", "surveillance", "warm", "bittersweet", "identity", "uncanny"],
                        [
                            "the movie where a guy's whole life is secretly a tv show",
                            "the one where he sails to the edge of the world and hits a wall",
                            "film about a man who doesn't know he's on camera",
                        ],
                        Spoiler: true)),

                Film(
                    "Memento", 2000,
                    ["Thriller", "Mystery"],
                    ["Guy Pearce", "Carrie-Anne Moss", "Christopher Nolan"],
                    "A man with short-term memory loss attempts to track down his wife's murderer.",
                    new Enrichment(
                        "A man who cannot form new memories hunts his wife's killer using tattoos, photographs and notes he can no longer verify.",
                        ["the tattoos across his chest", "the polaroids", "the scenes running backwards"],
                        ["dark", "twisted", "disorienting", "puzzle", "grief", "unreliable", "bleak"],
                        [
                            "the one where the guy can't make new memories",
                            "that movie where he tattoos the clues on himself",
                            "film told backwards about a man hunting his wife's killer",
                        ],
                        Spoiler: true)),

                Film(
                    "Oldboy", 2003,
                    ["Thriller", "Mystery", "Action"],
                    ["Choi Min-sik", "Park Chan-wook"],
                    "After being kidnapped and imprisoned for fifteen years, a man is released and must find his captor in five days.",
                    new Enrichment(
                        "A man imprisoned for fifteen years without explanation is let out and given days to find out who did it, and the answer is worse than the imprisonment.",
                        ["the corridor hammer fight", "the live octopus", "the hypnotist"],
                        ["dark", "twisted", "brutal", "revenge", "disturbing", "operatic", "bleak"],
                        [
                            "the one where he's locked in a room for fifteen years",
                            "that brutal korean revenge film with the hallway fight",
                            "dark twisted thriller with a horrifying ending",
                        ],
                        Spoiler: true)),

                Film(
                    "Amélie", 2001,
                    ["Comedy", "Romance"],
                    ["Audrey Tautou", "Jean-Pierre Jeunet"],
                    "Amélie is an innocent and naive girl in Paris with her own sense of justice who decides to help those around her.",
                    new Enrichment(
                        "A shy Parisian waitress quietly engineers small kindnesses and disasters in other people's lives while avoiding her own.",
                        ["the skimming stones", "the garden gnome's travels", "the photo booth scraps"],
                        ["warm", "whimsical", "charming", "comfort watch", "romantic", "playful", "gentle"],
                        [
                            "the one about the french girl who secretly helps strangers",
                            "whimsical paris film with the garden gnome",
                            "warm feel-good romance with a green and red look",
                        ],
                        Spoiler: false)),

                Film(
                    "Paddington", 2014,
                    ["Family", "Comedy", "Adventure"],
                    ["Ben Whishaw", "Hugh Bonneville", "Sally Hawkins", "Paul King"],
                    "A young Peruvian bear travels to London in search of a home, and finds himself with the Brown family.",
                    new Enrichment(
                        "A polite bear from Peru arrives in London looking for somewhere to belong and is taken in by a wary family.",
                        ["the bathroom flooding", "the marmalade sandwiches under his hat", "the hard stare"],
                        ["warm", "funny", "gentle", "comfort watch", "family", "kind", "cosy"],
                        [
                            "the one with the polite bear in london",
                            "gentle family film about a bear who loves marmalade",
                            "cosy comfort watch for a rainy afternoon",
                        ],
                        Spoiler: false)),

                Film(
                    "Blade Runner", 1982,
                    ["Sci-Fi", "Thriller"],
                    ["Harrison Ford", "Rutger Hauer", "Ridley Scott"],
                    "A blade runner must pursue and terminate four replicants who stole a ship in space and have returned to Earth to find their creator.",
                    new Enrichment(
                        "A burnt-out detective hunts artificial people who have come back to Earth wanting more life from the man who built them.",
                        ["tears in rain", "the origami unicorn", "the endless neon rain"],
                        ["bleak", "melancholy", "noir", "rain", "identity", "dystopian", "slow", "beautiful"],
                        [
                            "the one with the replicants and the tears in rain speech",
                            "rainy neon sci-fi about hunting androids",
                            "bleak future noir with the origami unicorn",
                        ],
                        Spoiler: false)),
            };

            return documents;
        }

        private static ItemDocument Film(
            string title,
            int year,
            string[] genres,
            string[] people,
            string overview,
            Enrichment enrichment)
        {
            // Deterministic ids so a failing assertion names the same film every run.
            var id = new Guid(System.Security.Cryptography.MD5.HashData(
                System.Text.Encoding.UTF8.GetBytes(title)));

            return new ItemDocument(
                id,
                "Movie",
                title,
                string.Empty,
                year,
                genres,
                [],
                [],
                people,
                string.Empty,
                110,
                overview,
                enrichment);
        }
    }
}
