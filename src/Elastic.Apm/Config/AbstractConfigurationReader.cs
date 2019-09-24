using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Elastic.Apm.Helpers;
using Elastic.Apm.Logging;
using static Elastic.Apm.Config.ConfigConsts;

namespace Elastic.Apm.Config
{
	public abstract class AbstractConfigurationReader
	{
		private const string ThisClassName = nameof(AbstractConfigurationReader);

		private readonly IApmLogger _logger;
		private readonly LazyContextualInit<int> _cachedMaxBatchEventCount = new LazyContextualInit<int>();
		private readonly LazyContextualInit<int> _cachedMaxQueueEventCount = new LazyContextualInit<int>();
		private readonly LazyContextualInit<IReadOnlyList<Uri>> _cachedServerUrls = new LazyContextualInit<IReadOnlyList<Uri>>();

		protected AbstractConfigurationReader(IApmLogger logger, string dbgDerivedClassName) =>
			_logger = logger?.Scoped($"{ThisClassName} ({dbgDerivedClassName})");

		protected static ConfigurationKeyValue Kv(string key, string value, string origin) =>
			new ConfigurationKeyValue(key, value, origin);

		protected internal static bool TryParseLogLevel(string value, out LogLevel level)
		{
			level = default;
			if (string.IsNullOrEmpty(value)) return false;

			var retLevel = DefaultLogLevel();
			if (!retLevel.HasValue) return false;

			level = retLevel.Value;
			return true;

			LogLevel? DefaultLogLevel()
			{
				switch (value.ToLowerInvariant())
				{
					case "trace": return LogLevel.Trace;
					case "debug": return LogLevel.Debug;
					case "information":
					case "info": return LogLevel.Information;
					case "warning": return LogLevel.Warning;
					case "error": return LogLevel.Error;
					case "critical": return LogLevel.Critical;
					case "none": return LogLevel.None;
					default: return null;
				}
			}
		}

		protected string ParseSecretToken(ConfigurationKeyValue kv)
		{
			if (kv == null || string.IsNullOrEmpty(kv.Value)) return null;

			return kv.Value;
		}

		protected bool ParseCaptureHeaders(ConfigurationKeyValue kv) => ParseBoolOption(kv, DefaultValues.CaptureHeaders, "CaptureHeaders");

		protected LogLevel ParseLogLevel(ConfigurationKeyValue kv)
		{
			if (TryParseLogLevel(kv?.Value, out var level)) return level;

			if (kv?.Value == null)
				_logger?.Debug()?.Log("No log level provided. Defaulting to log level '{DefaultLogLevel}'", ConsoleLogger.DefaultLogLevel);
			else
			{
				_logger?.Error()
					?.Log("Failed parsing log level from {Origin}: {Key}, value: {Value}. Defaulting to log level '{DefaultLogLevel}'",
						kv.ReadFrom, kv.Key, kv.Value, ConsoleLogger.DefaultLogLevel);
			}

			return ConsoleLogger.DefaultLogLevel;
		}

		protected IReadOnlyList<Uri> ParseServerUrls(ConfigurationKeyValue kv) =>
			_cachedServerUrls.IfNotInited?.InitOrGet(() => ParseServerUrlsImpl(kv)) ?? _cachedServerUrls.Value;

		private IReadOnlyList<Uri> ParseServerUrlsImpl(ConfigurationKeyValue kv)
		{
			var list = new List<Uri>();
			if (kv == null || string.IsNullOrEmpty(kv.Value)) return LogAndReturnDefault().AsReadOnly();

			var uriStrings = kv.Value.Split(',');
			foreach (var u in uriStrings)
			{
				if (TryParseUri(u, out var uri))
				{
					list.Add(uri);
					continue;
				}

				_logger?.Error()?.Log("Failed parsing server URL from {Origin}: {Key}, value: {Value}", kv.ReadFrom, kv.Key, u);
			}

			if (list.Count > 1)
			{
				_logger?.Warning()
					?.Log(nameof(EnvVarNames.ServerUrls)
						+ " configuration option contains more than one URL which is not supported by the agent yet"
						+ " - only the first URL will be used."
						+ " Configuration option's source: {Origin}, key: `{Key}', value: `{Value}'."
						+ " The first URL: `{ApmServerUrl}'",
						kv.ReadFrom, kv.Key, kv.Value, list.First());
			}

			return list.Count == 0 ? LogAndReturnDefault().AsReadOnly() : list.AsReadOnly();

			List<Uri> LogAndReturnDefault()
			{
				list.Add(DefaultValues.ServerUri);
				_logger?.Debug()?.Log("Using default ServerUrl: {ServerUrl}", DefaultValues.ServerUri);
				return list;
			}

			bool TryParseUri(string u, out Uri uri)
			{
				// https://stackoverflow.com/a/33573337
				uri = null;
				if (!Uri.IsWellFormedUriString(u, UriKind.Absolute)) return false;
				if (!Uri.TryCreate(u, UriKind.Absolute, out uri)) return false;

				return uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;
			}
		}

		protected double ParseMetricsInterval(ConfigurationKeyValue kv)
		{
			string value;
			if (kv == null || string.IsNullOrWhiteSpace(kv.Value))
				value = DefaultValues.MetricsInterval;
			else
				value = kv.Value;

			double valueInMilliseconds;

			try
			{
				if (!TryParseTimeInterval(value, out valueInMilliseconds, TimeSuffix.S))
				{
					_logger?.Error()
						?.Log("Failed to parse provided metrics interval `{ProvidedMetricsInterval}' - " +
							"using default: {DefaultMetricsInterval}",
							value,
							DefaultValues.MetricsInterval);
					return DefaultValues.MetricsIntervalInMilliseconds;
				}
			}
			catch (ArgumentException e)
			{
				_logger?.Critical()
					?.LogException(e, "Failed to parse metrics interval, using default: {DefaultMetricsInterval}",
						DefaultValues.MetricsInterval);
				return DefaultValues.MetricsIntervalInMilliseconds;
			}

			// ReSharper disable once CompareOfFloatsByEqualityOperator - we compare to exactly zero here
			if (valueInMilliseconds == 0)
				return valueInMilliseconds;

			if (valueInMilliseconds < 0)
			{
				_logger?.Error()
					?.Log("Provided metrics interval `{ProvidedMetricsInterval}' is negative - " +
						"metrics collection will be disabled",
						value);
				return 0;
			}

			// ReSharper disable once InvertIf
			if (valueInMilliseconds < Constraints.MinMetricsIntervalInMilliseconds)
			{
				_logger?.Error()
					?.Log("Provided metrics interval `{ProvidedMetricsInterval}' is smaller than allowed minimum: {MinProvidedMetricsInterval}ms - " +
						"metrics collection will be disabled",
						value,
						Constraints.MinMetricsIntervalInMilliseconds);
				return 0;
			}

			return valueInMilliseconds;
		}

		protected int ParseStackTraceLimit(ConfigurationKeyValue kv)
		{
			if (kv == null || string.IsNullOrWhiteSpace(kv.Value))
				return DefaultValues.StackTraceLimit;

			if (int.TryParse(kv.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
				return result;

			_logger?.Error()
				?.Log("Failed to parse provided stack trace limit `{ProvidedStackTraceLimit}` - using default: {DefaultStackTraceLimit}",
					kv.Value, DefaultValues.StackTraceLimit);

			return DefaultValues.StackTraceLimit;
		}

		protected double ParseSpanFramesMinDurationInMilliseconds(ConfigurationKeyValue kv)
		{
			string value;
			if (kv == null || string.IsNullOrWhiteSpace(kv.Value))
				value = DefaultValues.SpanFramesMinDuration;
			else
				value = kv.Value;

			double valueInMilliseconds;

			try
			{
				if (!TryParseTimeInterval(value, out valueInMilliseconds, TimeSuffix.Ms))
				{
					_logger?.Error()
						?.Log("Failed to parse provided span frames minimum duration `{ProvidedSpanFramesMinDuration}' - " +
							"using default: {DefaultSpanFramesMinDuration}",
							value,
							DefaultValues.SpanFramesMinDuration);
					return DefaultValues.SpanFramesMinDurationInMilliseconds;
				}
			}
			catch (ArgumentException e)
			{
				_logger?.Critical()
					?.LogException(e, nameof(ArgumentException) + " thrown from TryParseTimeInterval which means a programming bug - " +
						"using default: {DefaultSpanFramesMinDuration}",
						DefaultValues.SpanFramesMinDuration);
				return DefaultValues.SpanFramesMinDurationInMilliseconds;
			}

			return valueInMilliseconds;
		}

		private int ParseMaxXyzEventCount(ConfigurationKeyValue kv, int defaultValue, string dbgOptionName)
		{
			if (kv == null || string.IsNullOrWhiteSpace(kv.Value))
			{
				_logger?.Debug()
					?.Log(dbgOptionName + " configuration option doesn't have a valid value - using default: {Default" + dbgOptionName + "}",
						defaultValue);
				return defaultValue;
			}

			if (!int.TryParse(kv.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedValue))
			{
				_logger?.Error()
					?.Log(
						"Failed to parse provided " + dbgOptionName + ": `{Provided" + dbgOptionName + "}` - using default: {Default" + dbgOptionName
						+ "}",
						kv.Value, defaultValue);

				return defaultValue;
			}

			// ReSharper disable once InvertIf
			if (parsedValue <= 0)
			{
				_logger?.Error()
					?.Log("Provided " + dbgOptionName + ": `{Provided" + dbgOptionName + "}` is invalid (it should be positive) - " +
						"using default: {Default" + dbgOptionName + "}",
						kv.Value, defaultValue);
				return defaultValue;
			}

			return parsedValue;
		}

		protected int ParseMaxBatchEventCount(ConfigurationKeyValue kv) =>
			_cachedMaxBatchEventCount.IfNotInited?.InitOrGet(() => ParseMaxXyzEventCount(kv, DefaultValues.MaxBatchEventCount, "MaxBatchEventCount"))
			?? _cachedMaxBatchEventCount.Value;

		protected int ParseMaxQueueEventCount(ConfigurationKeyValue kv) =>
			_cachedMaxQueueEventCount.IfNotInited?.InitOrGet(() => ParseMaxXyzEventCount(kv, DefaultValues.MaxQueueEventCount, "MaxQueueEventCount"))
			?? _cachedMaxQueueEventCount.Value;

		protected TimeSpan ParseFlushInterval(ConfigurationKeyValue kv) =>
			ParsePositiveOrZeroTimeIntervalInMillisecondsImpl(kv, TimeSuffix.S, TimeSpan.FromMilliseconds(DefaultValues.FlushIntervalInMilliseconds),
				"FlushInterval");

		private TimeSpan ParsePositiveOrZeroTimeIntervalInMillisecondsImpl(ConfigurationKeyValue kv, TimeSuffix defaultSuffix,
			TimeSpan defaultValue, string dbgOptionName
		)
		{
			var value = ParseTimeIntervalImpl(kv, defaultSuffix, defaultValue, dbgOptionName);

			// ReSharper disable once InvertIf
			if (value < TimeSpan.Zero)
			{
				_logger?.Error()
					?.Log("Provided " + dbgOptionName + ": `{Provided" + dbgOptionName + "}` is invalid (it should be positive or zero) - " +
						"using default: {Default" + dbgOptionName + "}",
						kv.Value, defaultValue);
				return defaultValue;
			}

			return value;
		}

		private TimeSpan ParseTimeIntervalImpl(ConfigurationKeyValue kv, TimeSuffix defaultSuffix, TimeSpan defaultValue, string dbgOptionName)
		{
			if (kv == null || string.IsNullOrWhiteSpace(kv.Value))
			{
				_logger?.Debug()
					?.Log(dbgOptionName + " configuration option doesn't have a valid value - using default: {Default" + dbgOptionName + "}",
						defaultValue);
				return defaultValue;
			}

			double valueInMilliseconds;

			try
			{
				if (!TryParseTimeInterval(kv.Value, out valueInMilliseconds, defaultSuffix))
				{
					_logger?.Error()
						?.Log("Failed to parse provided " + dbgOptionName + ": `{Provided" + dbgOptionName + "}' - " +
							"using default: {Default" + dbgOptionName + "}",
							kv.Value,
							defaultValue);
					return defaultValue;
				}
			}
			catch (ArgumentException ex)
			{
				_logger?.Critical()
					?.LogException(ex, "Exception thrown from TryParseTimeInterval which means a programming bug - " +
						"using default: {Default" + dbgOptionName + "}",
						defaultValue);
				return defaultValue;
			}

			return TimeSpan.FromMilliseconds(valueInMilliseconds);
		}

		private bool TryParseTimeInterval(string valueAsString, out double valueInMilliseconds, TimeSuffix defaultSuffix)
		{
			switch (valueAsString)
			{
				case string _ when valueAsString.Length >= 2 && valueAsString.Substring(valueAsString.Length - 2).ToLowerInvariant() == "ms":
					return TryParseFloatingPoint(valueAsString.Substring(0, valueAsString.Length - 2), out valueInMilliseconds);

				case string _ when char.ToLower(valueAsString.Last()) == 's':
					if (!TryParseFloatingPoint(valueAsString.Substring(0, valueAsString.Length - 1), out var valueInSeconds))
					{
						valueInMilliseconds = 0;
						return false;
					}
					valueInMilliseconds = TimeSpan.FromSeconds(valueInSeconds).TotalMilliseconds;
					return true;

				case string _ when char.ToLower(valueAsString.Last()) == 'm':
					if (!TryParseFloatingPoint(valueAsString.Substring(0, valueAsString.Length - 1), out var valueInMinutes))
					{
						valueInMilliseconds = 0;
						return false;
					}
					valueInMilliseconds = TimeSpan.FromMinutes(valueInMinutes).TotalMilliseconds;
					return true;
				default:
					if (!TryParseFloatingPoint(valueAsString, out var valueNoUnits))
					{
						valueInMilliseconds = 0;
						return false;
					}

					switch (defaultSuffix)
					{
						case TimeSuffix.M:
							valueInMilliseconds = TimeSpan.FromMinutes(valueNoUnits).TotalMilliseconds;
							break;
						case TimeSuffix.Ms:
							valueInMilliseconds = TimeSpan.FromMilliseconds(valueNoUnits).TotalMilliseconds;
							break;
						case TimeSuffix.S:
							valueInMilliseconds = TimeSpan.FromSeconds(valueNoUnits).TotalMilliseconds;
							break;
						default:
							throw new ArgumentException($"Unexpected TimeSuffix value: {defaultSuffix}", /* paramName: */ nameof(defaultSuffix));
					}

					return true;
			}
		}

		private AssemblyName DiscoverEntryAssemblyName()
		{
			var entryAssemblyName = Assembly.GetEntryAssembly()?.GetName();
			if (entryAssemblyName != null && !IsMsOrElastic(entryAssemblyName.GetPublicKeyToken()))
				return entryAssemblyName;

			return null;
		}

		protected virtual string DiscoverServiceName()
		{
			var entryAssemblyName = DiscoverEntryAssemblyName();
			if (entryAssemblyName != null) return entryAssemblyName.Name;

			var stackFrames = new StackTrace().GetFrames();
			if (stackFrames == null) return null;

			foreach (var frame in stackFrames)
			{
				var currentAssemblyName = frame?.GetMethod()?.DeclaringType?.Assembly.GetName();
				if (currentAssemblyName != null && !IsMsOrElastic(currentAssemblyName.GetPublicKeyToken())) return currentAssemblyName.Name;
			}

			return null;
		}

		internal static string AdaptServiceName(string originalName) =>
			originalName != null
				? Regex.Replace(originalName, "[^a-zA-Z0-9 _-]", "_")
				: null;

		protected string ParseServiceName(ConfigurationKeyValue kv)
		{
			var nameInConfig = kv.Value;
			if (!string.IsNullOrEmpty(nameInConfig))
			{
				var adaptedServiceName = AdaptServiceName(nameInConfig);

				if (nameInConfig == adaptedServiceName)
					_logger?.Warning()?.Log("Service name provided in configuration is {ServiceName}", nameInConfig);
				else
				{
					_logger?.Warning()
						?.Log("Service name provided in configuration ({ServiceNameInConfiguration}) was adapted to {ServiceName}", nameInConfig,
							adaptedServiceName);
				}
				return adaptedServiceName;
			}

			_logger?.Info()?.Log("The agent was started without a service name. The service name will be automatically discovered.");

			var discoveredName = AdaptServiceName(DiscoverServiceName());
			if (discoveredName != null)
			{
				_logger?.Info()
					?.Log("The agent was started without a service name. The automatically discovered service name is {ServiceName}", discoveredName);
				return discoveredName;
			}

			_logger?.Error()
				?.Log("Failed to discover service name, the service name will be '{DefaultServiceName}'." +
					" You can fix this by setting the service name to a specific value (e.g. by using the environment variable {ServiceNameVariable})",
					DefaultValues.UnknownServiceName, EnvVarNames.ServiceName);
			return DefaultValues.UnknownServiceName;
		}

		private string DiscoverServiceVersion()
		{
			var entryAssembly = Assembly.GetEntryAssembly();
			if (entryAssembly != null && !IsMsOrElastic(entryAssembly.GetName().GetPublicKeyToken()))
				return entryAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

			return null;
		}

		protected string ParseServiceVersion(ConfigurationKeyValue kv)
		{
			var versionInConfig = kv.Value;

			if (!string.IsNullOrEmpty(versionInConfig)) return versionInConfig;

			_logger?.Info()?.Log("The agent was started without a service version. The service version will be automatically discovered.");

			var discoveredVersion = DiscoverServiceVersion();
			if (discoveredVersion != null)
			{
				_logger?.Info()
					?.Log("The agent was started without a service version. The automatically discovered service version is {ServiceVersion}",
						discoveredVersion);
				return discoveredVersion;
			}

			_logger?.Warning()?.Log("Failed to discover service version, the service version will be omitted.");

			return null;
		}

		protected string ParseEnvironment(ConfigurationKeyValue kv)
		{
			if (kv == null || string.IsNullOrEmpty(kv.Value)) return null;

			return kv.Value;
		}

		private static bool TryParseFloatingPoint(string valueAsString, out double result) =>
			double.TryParse(valueAsString, NumberStyles.Float, CultureInfo.InvariantCulture, out result);

		protected double ParseTransactionSampleRate(ConfigurationKeyValue kv)
		{
			if (kv?.Value == null)
			{
				_logger?.Debug()
					?.Log("No transaction sample rate provided. Defaulting to '{DefaultTransactionSampleRate}'",
						DefaultValues.TransactionSampleRate);
				return DefaultValues.TransactionSampleRate;
			}

			if (!TryParseFloatingPoint(kv.Value, out var parsedValue))
			{
				_logger?.Error()
					?.Log("Failed to parse provided transaction sample rate `{ProvidedTransactionSampleRate}' - " +
						"using default: {DefaultTransactionSampleRate}",
						kv.Value,
						DefaultValues.TransactionSampleRate);
				return DefaultValues.TransactionSampleRate;
			}

			if (!Sampler.IsValidRate(parsedValue))
			{
				_logger?.Error()
					?.Log(
						"Provided transaction sample rate is invalid {ProvidedTransactionSampleRate} - " +
						"using default: {DefaultTransactionSampleRate}",
						parsedValue,
						DefaultValues.TransactionSampleRate);
				return DefaultValues.TransactionSampleRate;
			}

			_logger?.Debug()
				?.Log("Using provided transaction sample rate `{ProvidedTransactionSampleRate}' parsed as {ProvidedTransactionSampleRate}",
					kv.Value,
					parsedValue);
			return parsedValue;
		}

		protected int ParseTransactionMaxSpans(ConfigurationKeyValue kv)
		{
			if (kv?.Value == null)
			{
				_logger?.Debug()
					?.Log("No transaction max spans provided. Defaulting to '{DefaultTransactionMaxSpans}'",
						DefaultValues.TransactionMaxSpans);
				return DefaultValues.TransactionMaxSpans;
			}

			if (int.TryParse(kv.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
			{
				if (result < -1)
				{
					_logger?.Error()
						?.Log(
							"Provided transaction max spans '{ProvidedTransactionMaxSpans}' is invalid (only positive, '0' and '-1' numbers are allowed) - using default: '{DefaultTransactionMaxSpans}'",
							result, DefaultValues.TransactionMaxSpans);
					return DefaultValues.TransactionMaxSpans;
				}

				_logger?.Debug()
					?.Log("Using provided transaction max spans '{ProvidedTransactionMaxSpans}' parsed as '{ParsedTransactionMaxSpans}'",
						kv.Value, result);
				return result;
			}


			_logger?.Error()
				?.Log("Failed to parse provided transaction max spans '{ProvidedTransactionMaxSpans}' - using default: {DefaultTransactionMaxSpans}",
					kv.Value, DefaultValues.TransactionMaxSpans);

			return DefaultValues.TransactionMaxSpans;
		}

		internal static bool IsMsOrElastic(byte[] array)
		{
			var elasticToken = new byte[] { 174, 116, 0, 210, 193, 137, 207, 34 };
			var mscorlibToken = new byte[] { 183, 122, 92, 86, 25, 52, 224, 137 };
			var systemWebToken = new byte[] { 176, 63, 95, 127, 17, 213, 10, 58 };
			var systemPrivateCoreLibToken = new byte[] { 124, 236, 133, 215, 190, 167, 121, 142 };
			var msAspNetCoreHostingToken = new byte[] { 173, 185, 121, 56, 41, 221, 174, 96 };

			if (array.Length != 8)
				return false;

			var isMsCorLib = true;
			var isElasticApm = true;
			var isSystemWeb = true;
			var isSystemPrivateCoreLib = true;
			var isMsAspNetCoreHosting = true;

			for (var i = 0; i < 8; i++)
			{
				if (array[i] != elasticToken[i])
					isElasticApm = false;
				if (array[i] != mscorlibToken[i])
					isMsCorLib = false;
				if (array[i] != systemWebToken[i])
					isSystemWeb = false;
				if (array[i] != systemPrivateCoreLibToken[i])
					isSystemPrivateCoreLib = false;
				if (array[i] != msAspNetCoreHostingToken[i])
					isMsAspNetCoreHosting = false;

				if (!isMsCorLib && !isElasticApm && !isSystemWeb && !isSystemPrivateCoreLib && !isMsAspNetCoreHosting)
					return false;
			}

			return true;
		}

		protected string ParseCaptureBody(ConfigurationKeyValue kv)
		{
			var captureBodyInConfig = kv.Value;
			if (string.IsNullOrEmpty(captureBodyInConfig))
				return DefaultValues.CaptureBody;

			// ReSharper disable once InvertIf
			if (!SupportedValues.CaptureBodySupportedValues.Contains(captureBodyInConfig.ToLowerInvariant()))
			{
				_logger?.Error()
					?.Log(
						"The CaptureBody value that was provided ('{DefaultServiceName}') in the configuration is not allowed. request body will not be captured."
						+
						"The supported values are : ",
						captureBodyInConfig.ToLowerInvariant(), string.Join(", ", SupportedValues.CaptureBodySupportedValues));
				return DefaultValues.CaptureBody;
			}
			return captureBodyInConfig.ToLowerInvariant();
		}

		protected List<string> ParseCaptureBodyContentTypes(ConfigurationKeyValue kv, string captureBody)
		{
			var captureBodyContentTypesInConfig = kv.Value;

			//CaptureBodyContentTypes and CaptureBody are linked. Verify that in case CaptureBody is ON - then CaptureBodyContentTypes is not empty
			if (string.IsNullOrEmpty(captureBodyContentTypesInConfig) && captureBody != SupportedValues.CaptureBodyOff)
			{
				_logger?.Error()?.Log("Capture_Body is on but no content types are configured. Request bodies will not be captured.");
				return new List<string>();
			}

			var captureBodyContentTypes = new List<string>();
			if (captureBodyContentTypesInConfig != null)
				captureBodyContentTypes = captureBodyContentTypesInConfig.Split(',').Select(p => p.Trim()).ToList();

			return captureBodyContentTypes;
		}

		protected bool ParseCentralConfig(ConfigurationKeyValue kv) => ParseBoolOption(kv, DefaultValues.CentralConfig, "CentralConfig");

		private bool ParseBoolOption(ConfigurationKeyValue kv, bool defaultValue, string dbgOptionName)
		{
			if (kv == null || string.IsNullOrWhiteSpace(kv.Value))
			{
				_logger?.Debug()
					?.Log(dbgOptionName + " configuration option doesn't have a valid value - using default: {Default" + dbgOptionName + "}",
						defaultValue);
				return defaultValue;
			}

			// ReSharper disable once InvertIf
			if (!bool.TryParse(kv.Value, out var parsedValue))
			{
				_logger?.Error()
					?.Log(
						"Failed to parse provided " + dbgOptionName + ": `{Provided" + dbgOptionName + "}` - using default: {Default" + dbgOptionName
						+ "}",
						kv.Value, defaultValue);

				return defaultValue;
			}

			return parsedValue;
		}

		protected string ReadEnvVarValue(string envVarName) => Environment.GetEnvironmentVariable(envVarName)?.Trim();

		private enum TimeSuffix
		{
			M,
			Ms,
			S
		}
	}
}
