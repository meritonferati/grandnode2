﻿using Grand.Business.Core.Interfaces.Catalog.Categories;
using Grand.Business.Core.Interfaces.Catalog.Products;
using Grand.Business.Core.Interfaces.Checkout.Orders;
using Grand.Business.Core.Interfaces.Cms;
using Grand.Business.Core.Interfaces.Common.Directory;
using Grand.Business.Core.Interfaces.Common.Logging;
using Grand.Domain.Catalog;
using Grand.Domain.Logging;
using Grand.Domain.Orders;
using Grand.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using System.Globalization;

namespace Widgets.GoogleAnalytics.Components
{
    [ViewComponent(Name = "WidgetsGoogleAnalytics")]
    public class WidgetsGoogleAnalyticsViewComponent : ViewComponent
    {
        private readonly IWorkContext _workContext;
        private readonly ILogger _logger;
        private readonly GoogleAnalyticsEcommerceSettings _googleAnalyticsEcommerceSettings;
        private readonly ICookiePreference _cookiePreference;
        private readonly IServiceProvider _serviceProvider;

        public WidgetsGoogleAnalyticsViewComponent(IWorkContext workContext,
            ILogger logger,
            GoogleAnalyticsEcommerceSettings googleAnalyticsEcommerceSettings,
            ICookiePreference cookiePreference,
            IServiceProvider serviceProvider
            )
        {
            _workContext = workContext;
            _logger = logger;
            _googleAnalyticsEcommerceSettings = googleAnalyticsEcommerceSettings;
            _cookiePreference = cookiePreference;
            _serviceProvider = serviceProvider;
        }

        public async Task<IViewComponentResult> InvokeAsync(string widgetZone, object additionalData = null)
        {
            var globalScript = "";

            if (_googleAnalyticsEcommerceSettings.AllowToDisableConsentCookie)
            {
                var enabled = await _cookiePreference.IsEnable(_workContext.CurrentCustomer, _workContext.CurrentStore, GoogleAnalyticDefaults.ConsentCookieSystemName);
                if ((enabled.HasValue && !enabled.Value) || (!enabled.HasValue && !_googleAnalyticsEcommerceSettings.ConsentDefaultState))
                    return Content("");
            }

            var routeData = Url.ActionContext.RouteData;
            try
            {
                var controller = routeData.Values["controller"];
                var action = routeData.Values["action"];

                if (controller == null || action == null)
                    return Content("");

                //Special case, if we are in last step of checkout, we can use order total for conversion value
                if (controller.ToString().Equals("checkout", StringComparison.OrdinalIgnoreCase) &&
                    action.ToString().Equals("completed", StringComparison.OrdinalIgnoreCase))
                {
                    var lastOrder = await GetLastOrder();
                    globalScript += await GetEcommerceScript(lastOrder);
                }
                else
                {
                    globalScript += GetTrackingScript();
                }
            }
            catch (Exception ex)
            {
                await _logger.InsertLog(LogLevel.Error, "Error creating scripts for google ecommerce tracking", ex.ToString());
            }
            return View("Default", globalScript);
        }

        private async Task<Order> GetLastOrder()
        {
            var orderService = _serviceProvider.GetRequiredService<IOrderService>();
            var order = (await orderService.SearchOrders(storeId: _workContext.CurrentStore.Id,
                customerId: _workContext.CurrentCustomer.Id, pageSize: 1)).FirstOrDefault();
            return order;
        }

        private string GetTrackingScript()
        {
            var analyticsTrackingScript = _googleAnalyticsEcommerceSettings.TrackingScript + "\n";
            analyticsTrackingScript = analyticsTrackingScript.Replace("{GOOGLEID}", _googleAnalyticsEcommerceSettings.GoogleId);
            analyticsTrackingScript = analyticsTrackingScript.Replace("{ECOMMERCE}", "");
            return analyticsTrackingScript;
        }

        private async Task<string> GetEcommerceScript(Order order)
        {
            var usCulture = new CultureInfo("en-US");
            var analyticsTrackingScript = _googleAnalyticsEcommerceSettings.TrackingScript + "\n";
            analyticsTrackingScript = analyticsTrackingScript.Replace("{GOOGLEID}", _googleAnalyticsEcommerceSettings.GoogleId);

            if (order != null)
            {
                var countryService = _serviceProvider.GetRequiredService<ICountryService>();
                var country = await countryService.GetCountryById(order.BillingAddress?.CountryId);
                var state = country?.StateProvinces.FirstOrDefault(x => x.Id == order.BillingAddress?.StateProvinceId);

                var analyticsEcommerceScript = _googleAnalyticsEcommerceSettings.EcommerceScript + "\n";
                analyticsEcommerceScript = analyticsEcommerceScript.Replace("{GOOGLEID}", _googleAnalyticsEcommerceSettings.GoogleId);
                analyticsEcommerceScript = analyticsEcommerceScript.Replace("{ORDERID}", order.Id.ToString());
                analyticsEcommerceScript = analyticsEcommerceScript.Replace("{TOTAL}", order.OrderTotal.ToString("0.00", usCulture));
                analyticsEcommerceScript = analyticsEcommerceScript.Replace("{CURRENCY}", order.CustomerCurrencyCode);
                analyticsEcommerceScript = analyticsEcommerceScript.Replace("{TAX}", order.OrderTax.ToString("0.00", usCulture));
                var orderShipping = _googleAnalyticsEcommerceSettings.IncludingTax ? order.OrderShippingInclTax : order.OrderShippingExclTax;
                analyticsEcommerceScript = analyticsEcommerceScript.Replace("{SHIP}", orderShipping.ToString("0.00", usCulture));
                analyticsEcommerceScript = analyticsEcommerceScript.Replace("{CITY}", order.BillingAddress == null ? "" : FixIllegalJavaScriptChars(order.BillingAddress.City));
                analyticsEcommerceScript = analyticsEcommerceScript.Replace("{STATEPROVINCE}", order.BillingAddress == null || String.IsNullOrEmpty(order.BillingAddress.StateProvinceId) ? "" : FixIllegalJavaScriptChars(state?.Name));
                analyticsEcommerceScript = analyticsEcommerceScript.Replace("{COUNTRY}", order.BillingAddress == null || String.IsNullOrEmpty(order.BillingAddress.CountryId) ? "" : FixIllegalJavaScriptChars(country?.Name));

                var sb = new StringBuilder();

                var productService = _serviceProvider.GetRequiredService<IProductService>();
                var categoryService = _serviceProvider.GetRequiredService<ICategoryService>();

                foreach (var item in order.OrderItems)
                {
                    var product = await productService.GetProductById(item.ProductId);
                    string analyticsEcommerceDetailScript = _googleAnalyticsEcommerceSettings.EcommerceDetailScript;
                    //get category
                    string category = "";
                    if (product.ProductCategories.Any())
                    {
                        var defaultProductCategory = await categoryService.GetCategoryById(product.ProductCategories.OrderBy(x => x.DisplayOrder).FirstOrDefault().CategoryId);
                        if (defaultProductCategory != null)
                            category = defaultProductCategory.Name;
                    }
                    analyticsEcommerceDetailScript = analyticsEcommerceDetailScript.Replace("{ORDERID}", order.Id.ToString());
                    //The SKU code is a required parameter for every item that is added to the transaction
                    analyticsEcommerceDetailScript = analyticsEcommerceDetailScript.Replace("{PRODUCTSKU}", FixIllegalJavaScriptChars(item.Sku));
                    analyticsEcommerceDetailScript = analyticsEcommerceDetailScript.Replace("{PRODUCTID}", FixIllegalJavaScriptChars(item.ProductId));
                    analyticsEcommerceDetailScript = analyticsEcommerceDetailScript.Replace("{PRODUCTNAME}", FixIllegalJavaScriptChars(product.Name));
                    analyticsEcommerceDetailScript = analyticsEcommerceDetailScript.Replace("{CATEGORYNAME}", FixIllegalJavaScriptChars(category));
                    var unitPrice = _googleAnalyticsEcommerceSettings.IncludingTax ? item.UnitPriceInclTax : item.UnitPriceExclTax;
                    analyticsEcommerceDetailScript = analyticsEcommerceDetailScript.Replace("{UNITPRICE}", unitPrice.ToString("0.00", usCulture));
                    analyticsEcommerceDetailScript = analyticsEcommerceDetailScript.Replace("{QUANTITY}", item.Quantity.ToString());
                    sb.AppendLine(analyticsEcommerceDetailScript);
                }

                analyticsEcommerceScript = analyticsEcommerceScript.Replace("{DETAILS}", sb.ToString());

                analyticsTrackingScript = analyticsTrackingScript.Replace("{ECOMMERCE}", analyticsEcommerceScript);

            }

            return analyticsTrackingScript;
        }

        private string FixIllegalJavaScriptChars(string text)
        {
            if (String.IsNullOrEmpty(text))
                return text;

            //replace ' with \' (http://stackoverflow.com/questions/4292761/need-to-url-encode-labels-when-tracking-events-with-google-analytics)
            text = text.Replace("'", "\\'");
            return text;
        }
    }
}