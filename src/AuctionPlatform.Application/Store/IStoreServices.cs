using AuctionPlatform.Application.Common;
using AuctionPlatform.Domain.Enums;

namespace AuctionPlatform.Application.Store;

/// <summary>Public catalog reads. Hidden and archived products never surface here.</summary>
public interface IStoreCatalogService
{
    Task<StoreHomeDto> GetHomeAsync(Guid? userId, CancellationToken ct = default);
    Task<PagedResult<StoreProductListItemDto>> GetProductsAsync(StoreProductQuery query, CancellationToken ct = default);
    Task<StoreProductDetailDto> GetProductAsync(Guid productId, CancellationToken ct = default);
    Task<IReadOnlyList<StoreCategoryDto>> GetCategoriesAsync(CancellationToken ct = default);
    Task<StoreCategoryDto> GetCategoryAsync(Guid categoryId, CancellationToken ct = default);
}

/// <summary>Checkout and order lifecycle from the customer's side.</summary>
public interface IStoreOrderService
{
    Task<CheckoutQuoteDto> QuoteAsync(Guid userId, CheckoutRequest request, CancellationToken ct = default);
    Task<StoreOrderDto> CheckoutAsync(Guid userId, CheckoutRequest request, CancellationToken ct = default);
    Task<PagedResult<StoreOrderDto>> GetMyOrdersAsync(Guid userId, StoreOrderQuery query, CancellationToken ct = default);
    Task<StoreOrderDto> GetOrderAsync(Guid userId, Guid orderId, CancellationToken ct = default);
    Task<StoreOrderDto> CancelOrderAsync(Guid userId, Guid orderId, string? reason, CancellationToken ct = default);

    Task<IReadOnlyList<ShippingAddressDto>> GetAddressesAsync(Guid userId, CancellationToken ct = default);
    Task<ShippingAddressDto> SaveAddressAsync(Guid userId, SaveShippingAddressRequest request, CancellationToken ct = default);
    Task DeleteAddressAsync(Guid userId, Guid addressId, CancellationToken ct = default);
}

/// <summary>Administrative catalog, inventory, order and settings management.</summary>
public interface IStoreAdminService
{
    Task<StoreOverviewDto> GetOverviewAsync(CancellationToken ct = default);

    Task<PagedResult<StoreProductListItemDto>> GetProductsAsync(StoreProductQuery query, CancellationToken ct = default);
    Task<StoreProductDetailDto> GetProductAsync(Guid productId, CancellationToken ct = default);
    Task<StoreProductDetailDto> CreateProductAsync(CreateStoreProductRequest request, CancellationToken ct = default);
    Task<StoreProductDetailDto> UpdateProductAsync(Guid productId, UpdateStoreProductRequest request, CancellationToken ct = default);
    Task<StoreProductDetailDto> SetProductStatusAsync(Guid productId, StoreProductStatus status, CancellationToken ct = default);
    Task DeleteProductAsync(Guid productId, CancellationToken ct = default);

    Task<IReadOnlyList<StoreCategoryDto>> GetCategoriesAsync(CancellationToken ct = default);
    Task<StoreCategoryDto> CreateCategoryAsync(CreateStoreCategoryRequest request, CancellationToken ct = default);
    Task<StoreCategoryDto> UpdateCategoryAsync(Guid categoryId, UpdateStoreCategoryRequest request, CancellationToken ct = default);
    Task DeleteCategoryAsync(Guid categoryId, CancellationToken ct = default);

    Task<PagedResult<InventoryRowDto>> GetInventoryAsync(StoreProductQuery query, CancellationToken ct = default);
    Task<InventoryTransactionDto> AdjustInventoryAsync(Guid productId, AdjustInventoryRequest request, CancellationToken ct = default);
    Task<PagedResult<InventoryTransactionDto>> GetInventoryHistoryAsync(Guid? productId, PagedQuery query, CancellationToken ct = default);

    Task<PagedResult<StoreOrderDto>> GetOrdersAsync(StoreOrderQuery query, CancellationToken ct = default);
    Task<StoreOrderDto> GetOrderAsync(Guid orderId, CancellationToken ct = default);
    Task<StoreOrderDto> TransitionOrderAsync(Guid orderId, StoreOrderStatus status, string? note, CancellationToken ct = default);
    Task<StoreOrderDto> RefundOrderAsync(Guid orderId, string reason, CancellationToken ct = default);

    Task<StoreSettingsDto> GetSettingsAsync(CancellationToken ct = default);
    Task<StoreSettingsDto> UpdateSettingsAsync(StoreSettingsDto settings, CancellationToken ct = default);
}
