using AuctionPlatform.Application.Common;
using AuctionPlatform.Application.Store;
using AuctionPlatform.Domain.Common;
using AuctionPlatform.Domain.Entities;
using AuctionPlatform.Domain.Enums;
using AuctionPlatform.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace AuctionPlatform.Tests.Integration;

/// <summary>
/// Departments in two levels: a main department (كوابل شحن), sub-departments under it
/// (كوابل شحن سيارة), and the products in either.
/// </summary>
[Collection("database")]
public class SubcategoryTests
{
    private readonly DatabaseFixture _fixture;

    public SubcategoryTests(DatabaseFixture fixture) => _fixture = fixture;

    private static string Unique(string name) => $"{name} {Guid.NewGuid().ToString("N")[..8]}";

    private async Task<StoreCategoryDto> CreateAsync(string name, Guid? parentId = null)
    {
        await using var scope = _fixture.CreateScope();
        var admin = scope.ServiceProvider.GetRequiredService<IStoreAdminService>();
        return await admin.CreateCategoryAsync(
            new CreateStoreCategoryRequest(Unique(name), null, null, null, 0, parentId));
    }

    private async Task<StoreProduct> AddProductAsync(Guid categoryId)
    {
        await using var scope = _fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var product = new StoreProduct
        {
            Name = "كيبل",
            Slug = $"test-{Guid.NewGuid():N}",
            Sku = $"TEST-{Guid.NewGuid():N}",
            CategoryId = categoryId,
            Price = 20m,
            Currency = Money.DefaultCurrency,
            StockQuantity = 5,
            Status = StoreProductStatus.Active,
            PaymentMode = StorePaymentMode.Both
        };

        db.StoreProducts.Add(product);
        await db.SaveChangesAsync();
        await scope.ServiceProvider.GetRequiredService<ICacheService>().RemoveByPrefixAsync("store:");
        return product;
    }

    [Fact]
    public async Task A_main_department_shows_its_own_products_and_its_sub_departments()
    {
        var cables = await CreateAsync("كوابل شحن");
        var car = await CreateAsync("كوابل شحن سيارة", cables.Id);
        var other = await CreateAsync("سماعات");

        var inMain = await AddProductAsync(cables.Id);
        var inSub = await AddProductAsync(car.Id);
        var elsewhere = await AddProductAsync(other.Id);

        await using var scope = _fixture.CreateScope();
        var catalog = scope.ServiceProvider.GetRequiredService<IStoreCatalogService>();

        var main = await catalog.GetProductsAsync(new StoreProductQuery { CategoryId = cables.Id, PageSize = 100 });
        main.Items.Select(p => p.Id).Should().BeEquivalentTo(new[] { inMain.Id, inSub.Id });

        var sub = await catalog.GetProductsAsync(new StoreProductQuery { CategoryId = car.Id, PageSize = 100 });
        sub.Items.Select(p => p.Id).Should().Equal(inSub.Id);

        var categories = await catalog.GetCategoriesAsync();
        categories.Single(c => c.Id == cables.Id).ProductCount.Should().Be(2, "its own and its sub-department's");
        var listedSub = categories.Single(c => c.Id == car.Id);
        listedSub.ParentId.Should().Be(cables.Id);
        listedSub.ParentName.Should().Be(cables.Name);

        var detail = await catalog.GetProductAsync(inSub.Id);
        detail.CategoryName.Should().Be(car.Name);
        detail.ParentCategoryId.Should().Be(cables.Id);
        detail.ParentCategoryName.Should().Be(cables.Name);

        elsewhere.Id.Should().NotBe(inSub.Id);
    }

    [Fact]
    public async Task Hiding_a_main_department_hides_its_sub_departments()
    {
        var cables = await CreateAsync("كوابل");
        var car = await CreateAsync("سيارة", cables.Id);

        await using (var scope = _fixture.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IStoreAdminService>().UpdateCategoryAsync(
                cables.Id, new UpdateStoreCategoryRequest(null, null, null, null, null, StoreCategoryStatus.Hidden));
        }

        await using var read = _fixture.CreateScope();
        var categories = await read.ServiceProvider.GetRequiredService<IStoreCatalogService>().GetCategoriesAsync();
        categories.Should().NotContain(c => c.Id == car.Id || c.Id == cables.Id);
    }

    [Fact]
    public async Task There_are_never_more_than_two_levels()
    {
        var main = await CreateAsync("رئيسي");
        var sub = await CreateAsync("فرعي", main.Id);

        // Under a sub-department: a third level.
        var third = () => CreateAsync("ثالث", sub.Id);
        (await third.Should().ThrowAsync<DomainException>()).Which.Code.Should().Be(ErrorCodes.CategoryTooDeep);

        await using var scope = _fixture.CreateScope();
        var admin = scope.ServiceProvider.GetRequiredService<IStoreAdminService>();

        // A main department with sub-departments cannot be moved under another.
        var other = await CreateAsync("آخر");
        var move = () => admin.UpdateCategoryAsync(main.Id,
            new UpdateStoreCategoryRequest(null, null, null, null, null, null, other.Id));
        (await move.Should().ThrowAsync<DomainException>()).Which.Code.Should().Be(ErrorCodes.CategoryTooDeep);

        // Nor under itself.
        var self = () => admin.UpdateCategoryAsync(other.Id,
            new UpdateStoreCategoryRequest(null, null, null, null, null, null, other.Id));
        (await self.Should().ThrowAsync<DomainException>()).Which.Code.Should().Be(ErrorCodes.CategoryTooDeep);
    }

    [Fact]
    public async Task A_sub_department_can_be_moved_and_made_a_main_department_again()
    {
        var a = await CreateAsync("أ");
        var b = await CreateAsync("ب");
        var sub = await CreateAsync("فرعي", a.Id);

        await using var scope = _fixture.CreateScope();
        var admin = scope.ServiceProvider.GetRequiredService<IStoreAdminService>();

        var moved = await admin.UpdateCategoryAsync(sub.Id,
            new UpdateStoreCategoryRequest(null, null, null, null, null, null, b.Id));
        moved.ParentId.Should().Be(b.Id);
        moved.ParentName.Should().Be(b.Name);

        // A plain edit that does not mention the parent leaves it alone.
        var renamed = await admin.UpdateCategoryAsync(sub.Id,
            new UpdateStoreCategoryRequest("اسم جديد " + Guid.NewGuid().ToString("N")[..6], null, null, null, null, null));
        renamed.ParentId.Should().Be(b.Id);

        var top = await admin.UpdateCategoryAsync(sub.Id,
            new UpdateStoreCategoryRequest(null, null, null, null, null, null, Guid.Empty));
        top.ParentId.Should().BeNull();
    }

    [Fact]
    public async Task A_main_department_with_sub_departments_cannot_be_deleted()
    {
        var main = await CreateAsync("رئيسي");
        await CreateAsync("فرعي", main.Id);

        await using var scope = _fixture.CreateScope();
        var delete = () => scope.ServiceProvider.GetRequiredService<IStoreAdminService>().DeleteCategoryAsync(main.Id);
        (await delete.Should().ThrowAsync<DomainException>()).Which.Code.Should().Be(ErrorCodes.CategoryHasSubcategories);
    }
}
