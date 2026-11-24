using System.Collections.Generic;
using System.Linq;
using EmbalmedEats.Core;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;

namespace EmbalmedEats.Recipes;

/// <summary>
/// Generates the wax dipping and resin lacquer crafting grid recipes: one shapeless recipe per
/// food per preservative ("food + beeswax"). Generated at server start from every item and block
/// in the game, so modded food is included automatically. They are ordinary grid recipes, so the
/// engine syncs them to clients and shows them in the handbook like any other recipe.
///
/// A food and preservative that another recipe already takes on their own get no recipe: the grid can offer only
/// one result for one set of ingredients, so one of the two could never be made. The game's own cheese waxing is
/// one (salted raw cheese + beeswax makes waxed cheese; Vintage Doctor 1.3.0 found the clash). That food can still
/// be preserved the other ways.
/// </summary>
public static class GridRecipeGenerator
{
    public static int Generated { get; private set; }
    public static readonly List<GridRecipe> Recipes = new();

    /// <summary>What was left out because another recipe takes the same two things: method, food, preservative, that recipe.</summary>
    public static readonly List<(string method, CollectibleObject food, CollectibleObject preservative, GridRecipe other)> Skipped = new();

    public static int Generate(ICoreServerAPI sapi)
    {
        Generated = 0;
        Recipes.Clear();
        Skipped.Clear();
        var cfg = Preservation.Config;
        var world = sapi.World;
        List<GridRecipe> twoThingRecipes = TwoThingRecipes(world);

        var methods = new List<(string method, string[] patterns)>();
        if (cfg.EnableWaxDipping) methods.Add((PreservationMethods.Wax, cfg.WaxItems));
        if (cfg.EnableResinLacquer) methods.Add((PreservationMethods.Resin, cfg.LacquerItems));
        if (methods.Count == 0) return 0;

        List<CollectibleObject> foods = world.Collectibles
            .Where(c => c?.Code != null && Preservation.IsPreservableCollectible(c))
            .ToList();

        foreach (var (method, patterns) in methods)
        {
            List<CollectibleObject> preservatives = world.Collectibles
                .Where(c => c?.Code != null && !c.IsMissing && Preservation.MatchesAny(patterns, c.Code))
                .ToList();

            if (preservatives.Count == 0)
            {
                sapi.Logger.Warning("[Embalmed Eats] No items found for {0} ({1}); that method has no recipes.", method, string.Join(", ", patterns ?? new string[0]));
                continue;
            }

            foreach (CollectibleObject preservative in preservatives)
            {
                List<GridRecipe> withIt = WithPreservative(twoThingRecipes, preservative); // a handful, so checking each food is quick
                foreach (CollectibleObject food in foods)
                {
                    if (food == preservative) continue;
                    GridRecipe? other = TakenBy(withIt, food, preservative);
                    if (other != null)
                    {
                        Skipped.Add((method, food, preservative, other));
                        sapi.Logger.Notification("[Embalmed Eats] No {0} recipe for {1}: {2} + {3} already make {4} ({5}).", method, food.Code, food.Code, preservative.Code,
                            other.Output?.Code, other.Name);
                        continue;
                    }

                    GridRecipe recipe = Build(method, food, preservative, cfg.PreservativePerFood);
                    recipe.OnParsed(world);
                    if (recipe.Resolve(world, "Embalmed Eats generated recipe"))
                    {
                        sapi.RegisterCraftingRecipe(recipe);
                        Recipes.Add(recipe);
                        Generated++;
                    }
                }
            }
        }

        RecipeBase.CollectiblePreSearchResultsCache.Clear();
        sapi.Logger.Notification("[Embalmed Eats] Generated {0} wax/resin crafting recipes for {1} preservable foods.", Generated, foods.Count);
        return Generated;
    }

    /// <summary>Every other recipe (not ours) made of exactly two things, whatever their places.</summary>
    static List<GridRecipe> TwoThingRecipes(IWorldAccessor world) => world.GridRecipes
        .Where(r => r != null && r.Enabled && r.Name?.Domain != "embalmedeats" && r.ResolvedIngredients != null
                    && r.ResolvedIngredients.Count(i => i != null) == 2)
        .ToList();

    /// <summary>Those of <paramref name="recipes"/> that take <paramref name="preservative"/> as one of their two things.</summary>
    static List<GridRecipe> WithPreservative(List<GridRecipe> recipes, CollectibleObject preservative)
    {
        var p = new ItemStack(preservative);
        return recipes.Where(r =>
        {
            try { return r.ResolvedIngredients.Any(i => i != null && i.SatisfiesAsIngredient(p, false)); }
            catch { return false; }
        }).ToList();
    }

    /// <summary>The other recipe that <paramref name="food"/> and <paramref name="preservative"/> on their own already make, if any.</summary>
    public static GridRecipe? TakenBy(IEnumerable<GridRecipe> twoThingRecipes, CollectibleObject food, CollectibleObject preservative)
    {
        var f = new ItemStack(food);
        var p = new ItemStack(preservative);
        foreach (GridRecipe r in twoThingRecipes)
        {
            CraftingRecipeIngredient[] two = r.ResolvedIngredients.Where(i => i != null).ToArray();
            try
            {
                if ((two[0].SatisfiesAsIngredient(f, false) && two[1].SatisfiesAsIngredient(p, false))
                    || (two[1].SatisfiesAsIngredient(f, false) && two[0].SatisfiesAsIngredient(p, false))) return r;
            }
            catch { } // a recipe the game can't compare isn't a reason to fail
        }
        return null;
    }

    /// <summary>The same check for the self test, against every recipe in the game now.</summary>
    public static GridRecipe? TakenBy(IWorldAccessor world, CollectibleObject food, CollectibleObject preservative) =>
        TakenBy(WithPreservative(TwoThingRecipes(world), preservative), food, preservative);

    static GridRecipe Build(string method, CollectibleObject food, CollectibleObject preservative, int preservativeQuantity)
    {
        var outputAttributes = new JObject
        {
            [Preservation.AttrKey] = new JObject { [Preservation.MethodKey] = method },
            [Preservation.TimeFrozenKey] = true
        };

        return new GridRecipe
        {
            Name = new AssetLocation("embalmedeats", $"grid/{method}/{preservative.Code.Domain}/{preservative.Code.Path}/{food.Code.Domain}/{food.Code.Path}"),
            IngredientPattern = "FP",
            Width = 2,
            Height = 1,
            Shapeless = true,
            ShowInCreatedBy = false,
            CopyAttributesFrom = "F",
            Attributes = new JsonObject(new JObject { ["embalmedeatsMethod"] = method }),
            Ingredients = new Dictionary<string, CraftingRecipeIngredient>
            {
                ["F"] = new CraftingRecipeIngredient { Type = food.ItemClass, Code = food.Code.Clone(), Quantity = 1 },
                ["P"] = new CraftingRecipeIngredient { Type = preservative.ItemClass, Code = preservative.Code.Clone(), Quantity = System.Math.Max(1, preservativeQuantity) }
            },
            Output = new CraftingRecipeIngredient
            {
                Type = food.ItemClass,
                Code = food.Code.Clone(),
                Quantity = 1,
                Attributes = new JsonObject(outputAttributes)
            }
        };
    }
}
