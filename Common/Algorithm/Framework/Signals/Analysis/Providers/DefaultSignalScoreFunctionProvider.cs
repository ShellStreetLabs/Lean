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
*/

using QuantConnect.Algorithm.Framework.Signals.Analysis.Functions;

namespace QuantConnect.Algorithm.Framework.Signals.Analysis.Providers
{
    /// <summary>
    /// Default implementation of <see cref="ISignalScoreFunctionProvider"/> always returns the <see cref="BinarySignalScoreFunction"/>
    /// </summary>
    public class DefaultSignalScoreFunctionProvider : ISignalScoreFunctionProvider
    {
        private static readonly BinarySignalScoreFunction Function = new BinarySignalScoreFunction();

        /// <inheritdoc />
        public ISignalScoreFunction GetScoreFunction(SignalType signalType, SignalScoreType scoreType)
        {
            return Function;
        }
    }
}