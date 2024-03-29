using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Web;
using System.Web.Routing;
using Nop.Core;
using Nop.Core.Domain.Directory;
using Nop.Core.Domain.Orders;
using Nop.Core.Domain.Payments;
using Nop.Core.Plugins;
using Nop.Plugin.Payments.PayGate.Controllers;
using Nop.Services.Configuration;
using Nop.Services.Directory;
using Nop.Services.Localization;
using Nop.Services.Orders;
using Nop.Services.Payments;
using Nop.Services.Tax;

using System.Collections.Specialized;
using System.Linq;
using System.Security.Cryptography;
using Nop.Core.Domain.Logging;
using Nop.Services.Logging;
using Nop.Web.Framework;

namespace Nop.Plugin.Payments.PayGate
{
    /// <summary>
    /// PayGate payment processor
    /// </summary>
    public class PayGatePaymentProcessor : BasePlugin, IPaymentMethod
    {
        #region Fields

        private readonly PayGatePaymentSettings _payGatePaymentSettings;
        private readonly ISettingService _settingService;
        private readonly ICurrencyService _currencyService;
        private readonly CurrencySettings _currencySettings;
        private readonly IWebHelper _webHelper;
        private readonly ICheckoutAttributeParser _checkoutAttributeParser;
        private readonly ITaxService _taxService;
        private readonly IOrderTotalCalculationService _orderTotalCalculationService;
        private readonly ILocalizationService _localizationService;
        private readonly HttpContextBase _httpContext;
        private Dictionary<string, string> dictionaryResponse;
        private readonly ILogger _logger;
        private readonly IOrderService _orderService;
        private readonly IStoreContext _storeContext;
        private IShoppingCartService _shoppingCartService;
        #endregion

        #region Ctor

        public PayGatePaymentProcessor(PayGatePaymentSettings payGatePaymentSettings,
            ISettingService settingService, ICurrencyService currencyService,
            CurrencySettings currencySettings, IWebHelper webHelper,
            ICheckoutAttributeParser checkoutAttributeParser, ITaxService taxService,
            IOrderTotalCalculationService orderTotalCalculationService, ILocalizationService localizationService,
            HttpContextBase httpContext, ILogger logger, IOrderService orderService,
            IStoreContext storeContext, IShoppingCartService shoppingCartService)
        {
            this._storeContext = storeContext;
            this._logger = logger;
            this._payGatePaymentSettings = payGatePaymentSettings;
            this._settingService = settingService;
            this._currencyService = currencyService;
            this._currencySettings = currencySettings;
            this._webHelper = webHelper;
            this._checkoutAttributeParser = checkoutAttributeParser;
            this._taxService = taxService;
            this._orderTotalCalculationService = orderTotalCalculationService;
            this._localizationService = localizationService;
            this._httpContext = httpContext;
            this._orderService = orderService;
            this._shoppingCartService = shoppingCartService;
        }

        #endregion

        #region Utilities

        #endregion

        #region Methods

        /// <summary>
        /// Process a payment
        /// </summary>
        /// <param name="processPaymentRequest">Payment info required for an order processing</param>
        /// <returns>Process payment result</returns>
        public ProcessPaymentResult ProcessPayment(ProcessPaymentRequest processPaymentRequest)
        {
            var result = new ProcessPaymentResult();
            result.NewPaymentStatus = PaymentStatus.Pending;
            return result;
        }

        /// <summary>
        /// Post process payment (used by payment gateways that require redirecting to a third-party URL)
        /// </summary>
        /// <param name="postProcessPaymentRequest">Payment info required for an order processing</param>
        public void PostProcessPayment(PostProcessPaymentRequest postProcessPaymentRequest)
        {
            var orderTotal = Math.Round(postProcessPaymentRequest.Order.OrderTotal, 2);
            var currentOrder = _orderService.GetOrderById(postProcessPaymentRequest.Order.Id);
            //_logger.InsertLog(LogLevel.Debug, "In process payment. Order ID " + postProcessPaymentRequest.Order.Id + " Order value: " + orderTotal);
            using (var client = new WebClient())
            {
                var initiateData = new NameValueCollection();
                initiateData["PAYGATE_ID"] = _payGatePaymentSettings.PayGateID;
                initiateData["REFERENCE"] = postProcessPaymentRequest.Order.Id.ToString();
                initiateData["AMOUNT"] = (Convert.ToDouble(orderTotal) * 100).ToString();
                initiateData["CURRENCY"] =
                    _currencyService.GetCurrencyByCode(currentOrder.CustomerCurrencyCode).CurrencyCode;
                initiateData["RETURN_URL"] = _webHelper.GetStoreLocation(false) + "Plugins/PaymentPayGate/PayGateReturnHandler?pgnopcommerce=" + postProcessPaymentRequest.Order.Id.ToString();
                initiateData["TRANSACTION_DATE"] = String.Format("{0:yyyy-MM-dd HH:mm:ss}", DateTime.Now).ToString();
                initiateData["LOCALE"] = "en-za";
                try
                {
                    initiateData["COUNTRY"] = postProcessPaymentRequest.Order.BillingAddress.Country.ThreeLetterIsoCode;
                    
                } catch (Exception ex)
                {
                    _logger.InsertLog(LogLevel.Error, ex.Message);
                    initiateData["COUNTRY"] = "ZAF";
                }
                try
                {
                    initiateData["EMAIL"] = postProcessPaymentRequest.Order.BillingAddress.Email;
                } catch (Exception ex)
                {
                    _logger.InsertLog(LogLevel.Error, ex.Message);
                    initiateData["EMAIL"] = "test@tom.com";
                }
                initiateData["USER3"] = "nopcommerce-v1.0.0";

                var initiateValues = string.Join("", initiateData.AllKeys.Select(key => initiateData[key]));
                //_logger.InsertLog(LogLevel.Debug, "Initiate values " + initiateValues);

                initiateData["CHECKSUM"] = new PayGateHelper().CalculateMD5Hash(initiateValues + _payGatePaymentSettings.EncryptionKey);
                //string initiateValuesCheck = string.Join("", initiateData.AllKeys.Select(key => initiateData[key]));
                //_logger.InsertLog(LogLevel.Debug, "Initiate values check " + initiateValuesCheck);

                var responseText = "";

                try
                {
                    _logger.InsertLog(LogLevel.Debug, "Trying initiate trans with values " + initiateValues);
                    var initiateResponse = client.UploadValues("https://secure.paygate.co.za/payweb3/initiate.trans", "POST", initiateData);
                    responseText = Encoding.Default.GetString(initiateResponse);
                    _logger.InsertLog(LogLevel.Debug, "Response text " + responseText);
                } catch (Exception excep)
                {
                    _logger.InsertLog(LogLevel.Error, "Exception in initiate: " + excep.Message);
                }               

                if (responseText.Contains("PGID_NOT_EN") || responseText.Contains("DATA_CUR") ||
                    responseText.Contains("DATA_PW") || responseText.Contains("DATA_CHK"))
                {
                    string Error = "Checksum posted does not match the one calculated by PayGate, either due to an incorrect encryption key used or a field that has been excluded from the checksum calculation";
                    if (responseText.Contains("PGID_NOT_EN"))
                    {
                        Error = "The PayGate ID being used to post data to PayGate has not yet been enabled, or there are no payment methods setup on it.";
                    }
                    else if (responseText.Contains("DATA_CUR"))
                    {
                        Error = "The currency that has been posted to PayGate is not supported.";
                    }
                    else if (responseText.Contains("DATA_PW"))
                    {
                        Error = "Mandatory fields have been excluded from the post to PayGate, refer to page 9 of the documentation as to what fields should be posted.";
                    }

                    _logger.InsertLog(LogLevel.Debug, "Paygate response for order number " + postProcessPaymentRequest.Order.Id.ToString(), Error);

                    
                    currentOrder.OrderStatus = OrderStatus.Cancelled;
                    _orderService.UpdateOrder(currentOrder);
                    

                    var url = _storeContext.CurrentStore.Url;
                    if (!url.EndsWith("/"))
                        url += "/";
                    var stringBuilder = "<html><head><script>function GoToUrl(){window.location = '" + url + "orderdetails/" + currentOrder.Id.ToString() + "';} GoToUrl();</script></head><body></body></html>";

                    _httpContext.Response.Write(stringBuilder);
                    _httpContext.Response.End();
                }
                else
                {
                    dictionaryResponse = responseText
                                         .Split('&')
                                         .Select(p => p.Split('='))
                                         .ToDictionary(p => p[0], p => p.Length > 1 ? Uri.UnescapeDataString(p[1]) : null);

                    if (dictionaryResponse["PAY_REQUEST_ID"] != null && dictionaryResponse["CHECKSUM"] != null)
                    {
                        _logger.InsertLog(LogLevel.Debug, "Attempting remote post");
                        RemotePost remotePost = new RemotePost
                        {
                            FormName = "PayGate",
                            Method = "POST",
                            Url = "https://secure.paygate.co.za/payweb3/process.trans"
                        };
                        remotePost.Add("PAY_REQUEST_ID", dictionaryResponse["PAY_REQUEST_ID"]);
                        remotePost.Add("CHECKSUM", dictionaryResponse["CHECKSUM"]);
                        remotePost.Post();
                    }
                }
            }
        }

        /// <summary>
        /// Returns a value indicating whether payment method should be hidden during checkout
        /// </summary>
        /// <param name="cart">Shoping cart</param>
        /// <returns>true - hide; false - display.</returns>
        public bool HidePaymentMethod(IList<ShoppingCartItem> cart)
        {
            //you can put any logic here
            //for example, hide this payment method if all products in the cart are downloadable
            //or hide this payment method if current customer is from certain country
            return false;
        }

        /// <summary>
        /// Gets additional handling fee
        /// </summary>
        /// <param name="cart">Shoping cart</param>
        /// <returns>Additional handling fee</returns>
        public decimal GetAdditionalHandlingFee(IList<ShoppingCartItem> cart) { return 0; }

        /// <summary>
        /// Captures payment
        /// </summary>
        /// <param name="capturePaymentRequest">Capture payment request</param>
        /// <returns>Capture payment result</returns>
        public CapturePaymentResult Capture(CapturePaymentRequest capturePaymentRequest)
        {
            var result = new CapturePaymentResult();
            result.AddError("Capture method not supported");
            return result;
        }

        /// <summary>
        /// Refunds a payment
        /// </summary>
        /// <param name="refundPaymentRequest">Request</param>
        /// <returns>Result</returns>
        public RefundPaymentResult Refund(RefundPaymentRequest refundPaymentRequest)
        {
            var result = new RefundPaymentResult();
            result.AddError("Refund method not supported");
            return result;
        }

        /// <summary>
        /// Voids a payment
        /// </summary>
        /// <param name="voidPaymentRequest">Request</param>
        /// <returns>Result</returns>
        public VoidPaymentResult Void(VoidPaymentRequest voidPaymentRequest)
        {
            var result = new VoidPaymentResult();
            result.AddError("Void method not supported");
            return result;
        }

        /// <summary>
        /// Process recurring payment
        /// </summary>
        /// <param name="processPaymentRequest">Payment info required for an order processing</param>
        /// <returns>Process payment result</returns>
        public ProcessPaymentResult ProcessRecurringPayment(ProcessPaymentRequest processPaymentRequest)
        {
            var result = new ProcessPaymentResult();
            result.AddError("Recurring payment not supported");
            return result;
        }

        /// <summary>
        /// Cancels a recurring payment
        /// </summary>
        /// <param name="cancelPaymentRequest">Request</param>
        /// <returns>Result</returns>
        public CancelRecurringPaymentResult CancelRecurringPayment(CancelRecurringPaymentRequest cancelPaymentRequest)
        {
            var result = new CancelRecurringPaymentResult();
            result.AddError("Recurring payment not supported");
            return result;
        }

        /// <summary>
        /// Gets a value indicating whether customers can complete a payment after order is placed but not completed (for redirection payment methods)
        /// </summary>
        /// <param name="order">Order</param>
        /// <returns>Result</returns>
        public bool CanRePostProcessPayment(Order order)
        {
            if (order == null)
                throw new ArgumentNullException("order");

            //let's ensure that at least 5 seconds passed after order is placed
            //P.S. there's no any particular reason for that. we just do it
            if ((DateTime.UtcNow - order.CreatedOnUtc).TotalSeconds < 5)
                return false;

            return true;
        }

        /// <summary>
        /// Gets a route for provider configuration
        /// </summary>
        /// <param name="actionName">Action name</param>
        /// <param name="controllerName">Controller name</param>
        /// <param name="routeValues">Route values</param>
        public void GetConfigurationRoute(out string actionName, out string controllerName, out RouteValueDictionary routeValues)
        {
            actionName = "Configure";
            controllerName = "PaymentPayGate";
            routeValues = new RouteValueDictionary { { "Namespaces", "Nop.Plugin.Payments.PayGate.Controllers" }, { "area", null } };
        }

        /// <summary>
        /// Gets a route for payment info
        /// </summary>
        /// <param name="actionName">Action name</param>
        /// <param name="controllerName">Controller name</param>
        /// <param name="routeValues">Route values</param>
        public void GetPaymentInfoRoute(out string actionName, out string controllerName, out RouteValueDictionary routeValues)
        {
            actionName = "PaymentInfo";
            controllerName = "PaymentPayGate";
            routeValues = new RouteValueDictionary { { "Namespaces", "Nop.Plugin.Payments.PayGate.Controllers" }, { "area", null } };
        }

        public Type GetControllerType()
        {
            return typeof(PaymentPayGateController);
        }

        public override void Install()
        {
            //settings
            var settings = new PayGatePaymentSettings
            {
                UseSandbox = true,
                PayGateID = "10011072130",
                EncryptionKey = "secret",
            };
            _settingService.SaveSetting(settings);

            //locales
            this.AddOrUpdatePluginLocaleResource("Plugins.Payments.PayGate.Fields.RedirectionTip", "You will be redirected to PayGate site to complete the order.");
            this.AddOrUpdatePluginLocaleResource("Plugins.Payments.PayGate.Fields.UseSandbox", "Use Sandbox");
            this.AddOrUpdatePluginLocaleResource("Plugins.Payments.PayGate.Fields.UseSandbox.Hint", "Check to enable Sandbox (testing environment).");
            this.AddOrUpdatePluginLocaleResource("Plugins.Payments.PayGate.Fields.PayGateID", "PayGate ID");
            this.AddOrUpdatePluginLocaleResource("Plugins.Payments.PayGate.Fields.PayGateID.Hint", "Specify your PayGate ID.");
            this.AddOrUpdatePluginLocaleResource("Plugins.Payments.PayGate.Fields.EncryptionKey", "Encryption Key");
            this.AddOrUpdatePluginLocaleResource("Plugins.Payments.PayGate.Fields.EncryptionKey.Hint", "Specify Encryption Key");
            this.AddOrUpdatePluginLocaleResource("Plugins.Payments.PayGate.Fields.EnableIpn", "Enable IPN (Instant Payment Notification)");
            this.AddOrUpdatePluginLocaleResource("Plugins.Payments.PayGate.Fields.EnableIpn.Hint", "Check if IPN is enabled.");
            this.AddOrUpdatePluginLocaleResource("Plugins.Payments.PayGate.Fields.EnableIpn.Hint2", "Leave blank to use the default IPN handler URL.");
            this.AddOrUpdatePluginLocaleResource("Plugins.Payments.PayGate.PaymentMethodDescription", "Pay by Credit/Debit Card");

            base.Install();
        }

        public override void Uninstall()
        {
            //settings
            _settingService.DeleteSetting<PayGatePaymentSettings>();

            //locales
            this.DeletePluginLocaleResource("Plugins.Payments.PayGate.Fields.RedirectionTip");
            this.DeletePluginLocaleResource("Plugins.Payments.PayGate.Fields.UseSandbox");
            this.DeletePluginLocaleResource("Plugins.Payments.PayGate.Fields.UseSandbox.Hint");
            this.DeletePluginLocaleResource("Plugins.Payments.PayGate.Fields.PayGateID");
            this.DeletePluginLocaleResource("Plugins.Payments.PayGate.Fields.PayGateID.Hint");
            this.DeletePluginLocaleResource("Plugins.Payments.PayGate.Fields.EncryptionKey");
            this.DeletePluginLocaleResource("Plugins.Payments.PayGate.Fields.EncryptionKey.Hint");
            this.DeletePluginLocaleResource("Plugins.Payments.PayGate.Fields.EnableIpn");
            this.DeletePluginLocaleResource("Plugins.Payments.PayGate.Fields.EnableIpn.Hint");
            this.DeletePluginLocaleResource("Plugins.Payments.PayGate.Fields.EnableIpn.Hint2");

            base.Uninstall();
        }

        #endregion

        #region Properties

        /// <summary>
        /// Gets a payment method description that will be displayed on checkout pages in the public store
        /// </summary>
        public string PaymentMethodDescription
        {
            //return description of this payment method to be display on "payment method" checkout step. good practice is to make it localizable
            //for example, for a redirection payment method, description may be like this: "You will be redirected to PayPal site to complete the payment"
            get { return _localizationService.GetResource("Plugins.Payments.PayGate.PaymentMethodDescription"); }
        }

        /// <summary>
        /// Gets a value indicating whether capture is supported
        /// </summary>
        public bool SupportCapture
        {
            get
            {
                return false;
            }
        }

        /// <summary>
        /// Gets a value indicating whether partial refund is supported
        /// </summary>
        public bool SupportPartiallyRefund
        {
            get
            {
                return false;
            }
        }

        /// <summary>
        /// Gets a value indicating whether refund is supported
        /// </summary>
        public bool SupportRefund
        {
            get
            {
                return false;
            }
        }

        /// <summary>
        /// Gets a value indicating whether void is supported
        /// </summary>
        public bool SupportVoid
        {
            get
            {
                return false;
            }
        }

        /// <summary>
        /// Gets a recurring payment type of payment method
        /// </summary>
        public RecurringPaymentType RecurringPaymentType
        {
            get
            {
                return RecurringPaymentType.NotSupported;
            }
        }

        /// <summary>
        /// Gets a payment method type
        /// </summary>
        public PaymentMethodType PaymentMethodType
        {
            get
            {
                return PaymentMethodType.Redirection;
            }
        }

        /// <summary>
        /// Gets a value indicating whether we should display a payment information page for this plugin
        /// </summary>
        public bool SkipPaymentInfo
        {
            get
            {
                return false;
            }
        }

        public string X2 { get; private set; }

        #endregion
    }
}
