﻿/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Lean Algorithmic Trading Engine v2.0. Copyright 2014 QuantConnect Corporation.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 *
*/

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using QuantConnect.Algorithm.Framework.Alphas;
using QuantConnect.Algorithm.Framework.Alphas.Analysis;
using QuantConnect.Algorithm.Framework.Alphas.Analysis.Providers;
using QuantConnect.Interfaces;
using QuantConnect.Lean.Engine.Alpha;
using QuantConnect.Logging;
using QuantConnect.Packets;

namespace QuantConnect.Lean.Engine.Alphas
{
    /// <summary>
    /// Base alpha handler that supports sending alphas to the messaging handler
    /// </summary>
    public class DefaultAlphaHandler : IAlphaHandler
    {
        private const int BacktestChartSamples = 1000;
        private static readonly IReadOnlyCollection<AlphaScoreType> ScoreTypes = AlphaManager.ScoreTypes;

        private DateTime _nextMessagingUpdate;
        private DateTime _nextPersistenceUpdate;
        private DateTime _nextChartSampleAlgorithmTimeUtc;
        private DateTime _lastChartSampleAlgorithmTimeUtc;

        private IMessagingHandler _messagingHandler;
        private CancellationTokenSource _cancellationTokenSource;
        private readonly ConcurrentQueue<Packet> _messages = new ConcurrentQueue<Packet>();

        private readonly Chart _assetBreakdownChart = new Chart("Alpha Asset Breakdown");
        private readonly Series _predictionCountSeries = new Series("Count", SeriesType.Line, "#");
        private readonly ConcurrentDictionary<Symbol, int> _alphaCountPerSymbol = new ConcurrentDictionary<Symbol, int>();
        private readonly Dictionary<AlphaScoreType, Series> _seriesByScoreType = new Dictionary<AlphaScoreType, Series>();

        /// <inheritdoc />
        public bool IsActive => !_cancellationTokenSource?.IsCancellationRequested ?? false;

        /// <summary>
        /// Gets the algorithm's unique identifier
        /// </summary>
        protected string AlgorithmId => Job.AlgorithmId;

        /// <summary>
        /// Gets whether or not the job is a live job
        /// </summary>
        protected bool LiveMode => Job is LiveNodePacket;

        /// <summary>
        /// Gets the algorithm job packet
        /// </summary>
        protected AlgorithmNodePacket Job { get; private set; }

        /// <summary>
        /// Gets the algorithm instance
        /// </summary>
        protected IAlgorithm Algorithm { get; private set; }

        /// <summary>
        /// Gets or sets the interval at which the alphas are persisted
        /// </summary>
        protected TimeSpan PersistenceUpdateInterval { get; set; } = TimeSpan.FromMinutes(1);

        /// <summary>
        /// Gets or sets the interval at which alpha updates are sent to the messaging handler
        /// </summary>
        protected TimeSpan MessagingUpdateInterval { get; set; } = TimeSpan.FromSeconds(2);

        /// <summary>
        /// Gets or sets the interval at which alpha charts are updated. This is in realtion to algorithm time.
        /// </summary>
        protected TimeSpan ChartUpdateInterval { get; set; } = TimeSpan.FromMinutes(1);

        /// <summary>
        /// Gets the alpha manager instance used to manage the analysis of algorithm alphas
        /// </summary>
        protected AlphaManager AlphaManager { get; private set; }

        /// <inheritdoc />
        public virtual void Initialize(AlgorithmNodePacket job, IAlgorithm algorithm, IMessagingHandler messagingHandler, IApi api)
        {
            Job = job;
            Algorithm = algorithm;
            _messagingHandler = messagingHandler;
            AlphaManager = CreateAlphaManager();
            algorithm.AlphasGenerated += (algo, collection) => OnAlphasGenerated(collection);

            // chart for average scores over sample period
            var scoreChart = new Chart("Alpha");
            foreach (var scoreType in ScoreTypes)
            {
                var series = new Series($"{scoreType} Score", SeriesType.Line, "%");
                scoreChart.AddSeries(series);
                _seriesByScoreType[scoreType] = series;
            }

            // chart for prediction count over sample period
            var predictionCount = new Chart("Alpha Count");
            predictionCount.AddSeries(_predictionCountSeries);

            Algorithm.AddChart(scoreChart);
            Algorithm.AddChart(predictionCount);
            Algorithm.AddChart(_assetBreakdownChart);
        }

        /// <inheritdoc />
        public void OnAfterAlgorithmInitialized(IAlgorithm algorithm)
        {
            _lastChartSampleAlgorithmTimeUtc = algorithm.UtcTime;
            if (!LiveMode)
            {
                // space out backtesting samples evenly
                var backtestPeriod = algorithm.EndDate - algorithm.StartDate;
                ChartUpdateInterval = TimeSpan.FromTicks(backtestPeriod.Ticks / BacktestChartSamples);
            }
            else
            {
                // live mode we'll sample each minute
                ChartUpdateInterval = Time.OneMinute;
            }
        }

        /// <inheritdoc />
        public virtual void ProcessSynchronousEvents()
        {
            // before updating scores, emit chart points for the previous sample period
            if (Algorithm.UtcTime >= _nextChartSampleAlgorithmTimeUtc)
            {
                try
                {
                    UpdateCharts();
                }
                catch (Exception err)
                {
                    Log.Error(err);
                }
            }

            try
            {
                // update scores in line with the algo thread to ensure a consistent read of security data
                // this will manage marking alphas as closed as well as performing score updates
                AlphaManager.UpdateScores();
            }
            catch (Exception err)
            {
                Log.Error(err);
            }
        }

        private void UpdateCharts()
        {
            var updatedAlphas = AlphaManager.AllAlphas.Where(alpha =>
                alpha.Score.UpdatedTimeUtc >= _lastChartSampleAlgorithmTimeUtc &&
                alpha.Score.UpdatedTimeUtc <= _nextChartSampleAlgorithmTimeUtc
            )
            .ToList();

            ChartAverageAlphaScores(updatedAlphas, Algorithm.UtcTime);

            // compute and chart total alpha count over sample period
            var totalAlphas = _alphaCountPerSymbol.Values.Sum();
            _predictionCountSeries.AddPoint(Algorithm.UtcTime, totalAlphas, LiveMode);

            // chart asset breakdown over sample period
            foreach (var kvp in _alphaCountPerSymbol)
            {
                var symbol = kvp.Key;
                var count = kvp.Value;

                Series series;
                if (!_assetBreakdownChart.Series.TryGetValue(symbol.Value, out series))
                {
                    series = new Series(symbol.Value, SeriesType.StackedArea, "#");
                    _assetBreakdownChart.Series.Add(series.Name, series);
                }

                series.AddPoint(Algorithm.UtcTime, count, LiveMode);
            }

            // reset for next sampling period
            _alphaCountPerSymbol.Clear();
            _lastChartSampleAlgorithmTimeUtc = _nextChartSampleAlgorithmTimeUtc;
            _nextChartSampleAlgorithmTimeUtc = Algorithm.UtcTime + ChartUpdateInterval;
        }

        /// <inheritdoc />
        public virtual void Run()
        {
            _cancellationTokenSource = new CancellationTokenSource();

            // run until cancelled AND we're done processing messages
            while (!_cancellationTokenSource.IsCancellationRequested || !_messages.IsEmpty)
            {
                try
                {
                    ProcessAsynchronousEvents();
                }
                catch (Exception err)
                {
                    Log.Error(err);
                    throw;
                }

                Thread.Sleep(50);
            }

            // persist alphas at exit
            StoreAlphas();
        }

        /// <inheritdoc />
        public void Exit()
        {
            // send final alpha scoring updates before we exit
            _messages.Enqueue(new AlphaPacket
            {
                AlgorithmId = AlgorithmId,
                Alphas = AlphaManager.GetUpdatedContexts().Select(context => context.Alpha).ToList()
            });

            _cancellationTokenSource.Cancel(false);
        }

        /// <summary>
        /// Performs asynchronous processing, including broadcasting of alphas to messaging handler
        /// </summary>
        protected void ProcessAsynchronousEvents()
        {
            Packet packet;
            while (_messages.TryDequeue(out packet))
            {
                _messagingHandler.Send(packet);
            }

            // persist generated alphas to storage
            if (DateTime.UtcNow > _nextPersistenceUpdate)
            {
                StoreAlphas();
                _nextPersistenceUpdate = DateTime.UtcNow + PersistenceUpdateInterval;
            }

            // push updated alphas through messaging handler
            if (DateTime.UtcNow > _nextMessagingUpdate)
            {
                var alphas = AlphaManager.GetUpdatedContexts().Select(context => context.Alpha).ToList();
                if (alphas.Count > 0)
                {
                    _messages.Enqueue(new AlphaPacket
                    {
                        AlgorithmId = AlgorithmId,
                        Alphas = alphas
                    });
                }
                _nextMessagingUpdate = DateTime.UtcNow + MessagingUpdateInterval;
            }
        }

        /// <summary>
        /// Save alpha results to persistent storage
        /// </summary>
        protected virtual void StoreAlphas()
        {
            // default save all results to disk and don't remove any from memory
            // this will result in one file with all of the alphas/results in it
            var alphas = AlphaManager.AllAlphas.OrderBy(alpha => alpha.GeneratedTimeUtc).ToList();
            if (alphas.Count > 0)
            {
                var path = Path.Combine(Directory.GetCurrentDirectory(), AlgorithmId, "alpha-results.json");
                Directory.CreateDirectory(new FileInfo(path).DirectoryName);
                File.WriteAllText(path, JsonConvert.SerializeObject(alphas, Formatting.Indented));
            }
        }

        /// <summary>
        /// Handles the algorithm's <see cref="IAlgorithm.AlphasGenerated"/> event
        /// and broadcasts the new alpha using the messaging handler
        /// </summary>
        protected void OnAlphasGenerated(AlphaCollection collection)
        {
            // send message for newly created alphas
            Packet packet = new AlphaPacket(AlgorithmId, collection.Alphas);
            _messages.Enqueue(packet);

            AlphaManager.AddAlphas(collection);

            // aggregate alpha counts per symbol
            foreach (var grouping in collection.Alphas.GroupBy(alpha => alpha.Symbol))
            {
                _alphaCountPerSymbol.AddOrUpdate(grouping.Key, 1, (sym, cnt) => cnt + grouping.Count());
            }
        }

        /// <summary>
        /// Creates the <see cref="AlphaManager"/> to manage the analysis of generated alphas
        /// </summary>
        /// <returns>A new alpha manager instance</returns>
        protected virtual AlphaManager CreateAlphaManager()
        {
            var scoreFunctionProvider = new DefaultAlphaScoreFunctionProvider();
            return new AlphaManager(new AlgorithmSecurityValuesProvider(Algorithm), scoreFunctionProvider, 0);
        }

        /// <summary>
        /// Adds chart point for each alpha score type with an average value of the specified period
        /// </summary>
        /// <param name="alphas">The alphas to chart average scores for</param>
        /// <param name="end">The analysis end time, used as time for chart points</param>
        protected void ChartAverageAlphaScores(List<Algorithm.Framework.Alphas.Alpha> alphas, DateTime end)
        {
            // compute average score values for all alphas with updates over the last day
            var count = 0;
            var runningScoreTotals = ScoreTypes.ToDictionary(type => type, type => 0d);

            // ignore alphas that haven't received scoring updates yet
            foreach (var alpha in alphas.Where(alpha => alpha.GeneratedTimeUtc != alpha.Score.UpdatedTimeUtc))
            {
                count++;
                foreach (var scoreType in ScoreTypes)
                {
                    runningScoreTotals[scoreType] += alpha.Score.GetScore(scoreType);
                }
            }

            if (count < 1)
            {
                return;
            }

            foreach (var kvp in runningScoreTotals)
            {
                var scoreType = kvp.Key;
                var runningTotal = kvp.Value;
                var average = runningTotal / count;
                // scale the value from [0,1] to [0,100] for charting
                var scoreToPlot = 100 * average;

                // ensure we're not violating the decimal type's range before plotting
                if (Math.Abs(scoreToPlot) > (double) Extensions.GetDecimalEpsilon() &&
                    Math.Abs(scoreToPlot) < (double) decimal.MaxValue)
                {
                    _seriesByScoreType[scoreType].AddPoint(end, (decimal) scoreToPlot, LiveMode);
                }
            }
        }
    }
}