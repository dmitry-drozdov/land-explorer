using System.Collections.Generic;
using MarkupServer;

namespace MarkupServer.Tests;

/// <summary>
/// Тесты SoftPairScore. Покрывают ключевые сценарии из Python-прототипа
/// (markup/experiments/coupled_rebind/fixtures.py), в первую очередь — те,
/// которые отвечают за корректность скоринга пары.
///
/// Тестируем именно SoftPairScore.Score (а не RPC-уровень) — это самая
/// нетривиальная часть и она чистая функция от двух TreeNode.
/// </summary>
[TestClass]
public class CoupledRebindTests
{
    // ---------- Хелперы создания TreeNode-фикстур ----------

    private static TreeNode Gql(
        string name,
        string parent = "Query",
        string returns = "User!",
        List<Arg> args = null,
        string file = "schema/users.graphql")
        => new()
        {
            Id = $"gql:{parent}.{name}",
            Language = "gql",
            NodeType = "anchor",
            AnchorKind = "gqlField",
            Name = name,
            MethodNameNorm = name,
            ParentNameRaw = parent,
            ParentNameNorm = parent,
            ReturnTypeNorm = returns,
            Args = args ?? new List<Arg> { new() { NameNorm = "id", TypeNorm = "ID!" } },
            Filepath = file,
        };

    private static TreeNode Ts(
        string name,
        string parent = "Query",
        string returns = "User",
        List<Arg> args = null,
        string file = "src/resolvers/userResolver.ts")
        => new()
        {
            Id = $"ts:{parent}.{name}",
            Language = "ts",
            NodeType = "anchor",
            AnchorKind = "tsMember",
            Name = name,
            MethodNameNorm = name,
            ParentNameRaw = parent,
            ParentNameNorm = parent,
            ReturnTypeNorm = returns,
            Args = args ?? new List<Arg> { new() { NameNorm = "id", TypeNorm = "ID" } },
            Filepath = file,
        };

    // ---------- 1. Нормализация имён ----------

    [TestMethod]
    public void Norm_camelCase_to_words()
    {
        Assert.AreEqual("fetch user by id", SoftPairScore.Norm("fetchUserById"));
        Assert.AreEqual("get user posts", SoftPairScore.Norm("get_user_posts"));
        Assert.AreEqual("xml parser", SoftPairScore.Norm("XMLParser"));
        Assert.AreEqual("user", SoftPairScore.Norm("User!"));
        Assert.AreEqual("post", SoftPairScore.Norm("[Post!]!"));
        Assert.AreEqual("", SoftPairScore.Norm(""));
    }

    [TestMethod]
    public void Levenshtein_basic()
    {
        Assert.AreEqual(0, SoftPairScore.Levenshtein("abc", "abc"));
        Assert.AreEqual(3, SoftPairScore.Levenshtein("abc", "xyz"));
        Assert.AreEqual(1, SoftPairScore.Levenshtein("kitten", "kittten"));
        Assert.AreEqual(3, SoftPairScore.Levenshtein("kitten", "sitting"));
    }

    [TestMethod]
    public void LevSimilarity_in_unit_range()
    {
        Assert.AreEqual(1.0, SoftPairScore.LevSimilarity("abc", "abc"), 1e-9);
        Assert.AreEqual(0.0, SoftPairScore.LevSimilarity("", "abc"), 1e-9);
        Assert.AreEqual(1.0, SoftPairScore.LevSimilarity("", ""), 1e-9);
        var s = SoftPairScore.LevSimilarity("kitten", "sitting");
        Assert.IsTrue(s > 0.0 && s < 1.0);
    }

    // ---------- 2. Pair compatibility — каноничные случаи из python-фикстур ----------

    [TestMethod]
    public void Perfect_pair_score_is_one()
    {
        // identical_001: getUser ↔ getUser, всё совпадает
        var a = Gql("getUser");
        var b = Ts("getUser");
        var s = SoftPairScore.Score(a, b);
        Assert.AreEqual(1.0, s, 1e-3, "Идеальная пара должна дать 1.0");
    }

    [TestMethod]
    public void Sync_rename_pair_is_still_strong()
    {
        // sync_rename_001: getUser → fetchUserById с обеих сторон
        var a = Gql("fetchUserById");
        var b = Ts("fetchUserById");
        var s = SoftPairScore.Score(a, b);
        Assert.AreEqual(1.0, s, 1e-3, "Синхронный rename даёт идеальную пару");
    }

    [TestMethod]
    public void Hub_pair_score_is_low()
    {
        // hub_001 (изолированная проверка): fetchUserById ↔ handle (middleware)
        var a = Gql("fetchUserById");
        var b = Ts("handle", parent: "Query", returns: "void",
                   args: new List<Arg>
                   {
                       new() { NameNorm = "req", TypeNorm = "Request" },
                       new() { NameNorm = "res", TypeNorm = "Response" },
                   },
                   file: "src/middleware/handlers.ts");
        var s = SoftPairScore.Score(a, b);
        Assert.IsTrue(s < 0.40, $"Хаб должен иметь низкий S2, получили {s:F3}");
    }

    [TestMethod]
    public void Similar_competitor_is_medium()
    {
        // ambiguous_001: fetchUserById ↔ getUserPosts (другая пара, но похожая)
        var a = Gql("fetchUserById");
        var b = Ts("getUserPosts",
                   returns: "Post[]",
                   args: new List<Arg> { new() { NameNorm = "userId", TypeNorm = "ID" } },
                   file: "src/resolvers/postResolver.ts");
        var s = SoftPairScore.Score(a, b);
        Assert.IsTrue(s > 0.40 && s < 0.80,
            $"Похожий конкурент должен дать средний S2, получили {s:F3}");
    }

    [TestMethod]
    public void Cross_language_same_lang_returns_zero()
    {
        // Hard cut-off: обе стороны одного языка
        var a = Gql("getUser");
        var b = Gql("getUser");
        Assert.AreEqual(0.0, SoftPairScore.Score(a, b), 1e-9);
    }

    [TestMethod]
    public void Parent_match_loose()
    {
        // parent_renamed: Query vs QueryResolver — loose match через .Contains
        var a = Gql("getUser", parent: "Query");
        var b = Ts("getUser", parent: "QueryResolver");
        var s = SoftPairScore.Score(a, b);
        // parent_sim даст 0.5 (loose), остальное идеально → итог около 0.875
        Assert.IsTrue(s > 0.80, $"Loose parent match должен дать высокий S2, получили {s:F3}");
    }

    [TestMethod]
    public void Tightened_returns_pair_still_high()
    {
        // adversarial_tightened_returns: User → User! (стал NonNull)
        var a = Gql("getUser", returns: "User!");      // NonNull
        var b = Ts("getUser", returns: "User");        // в TS просто User
        var s = SoftPairScore.Score(a, b);
        Assert.IsTrue(s > 0.90,
            $"Returns с разным NonNull-маркером должны нормализоваться до одного, получили {s:F3}");
    }

    [TestMethod]
    public void Snake_camel_normalization_pair()
    {
        // snake_case_001: get_user ↔ getUser должны быть совместимы
        var a = Gql("get_user");
        var b = Ts("getUser");
        var s = SoftPairScore.Score(a, b);
        Assert.AreEqual(1.0, s, 1e-3,
            "snake_case и camelCase должны нормализоваться к одинаковому имени");
    }

    [TestMethod]
    public void Asymmetric_args_lowers_score()
    {
        // adversarial_add_arg_one_side: на gql добавили arg, в ts нет
        var aFull = Gql("getUser",
            args: new List<Arg>
            {
                new() { NameNorm = "id", TypeNorm = "ID!" },
                new() { NameNorm = "tenant", TypeNorm = "String!" },
            });
        var bShort = Ts("getUser",
            args: new List<Arg> { new() { NameNorm = "id", TypeNorm = "ID" } });
        var s = SoftPairScore.Score(aFull, bShort);
        // Имя и parent совпадают, но args сильно расходятся → ≈ 0.85-0.95
        Assert.IsTrue(s > 0.80,
            $"Рассинхрон в одном arg не должен ронять S2 ниже 0.80, получили {s:F3}");
    }

    [TestMethod]
    public void File_bonus_only_for_resolver_path()
    {
        var a = Gql("getUser");
        var bIn = Ts("getUser", file: "src/resolvers/userResolver.ts");
        var bOut = Ts("getUser", file: "src/services/userService.ts");

        var sIn = SoftPairScore.Score(a, bIn);
        var sOut = SoftPairScore.Score(a, bOut);
        // file_bonus имеет вес 0.10, поэтому разница ≈ 0.10
        Assert.IsTrue(sIn - sOut > 0.05 && sIn - sOut < 0.15,
            $"File bonus должен давать разницу ≈0.10, получили {sIn - sOut:F3}");
    }
}
