/*
Copyright 2017 Coin Foundry (coinfoundry.org)
Authors: Oliver Weichhold (oliver@weichhold.com)

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and
associated documentation files (the "Software"), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial
portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT
LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.
IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY,
WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE
SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using Autofac;
using AutoMapper;
using Miningcore.Blockchain.Cryptonote.Configuration;
using Miningcore.Blockchain.Cryptonote.DaemonRequests;
using Miningcore.Blockchain.Cryptonote.DaemonResponses;
using Miningcore.Configuration;
using Miningcore.DaemonInterface;
using Miningcore.Extensions;
using Miningcore.Messaging;
using Miningcore.Native;
using Miningcore.Payments;
using Miningcore.Persistence;
using Miningcore.Persistence.Model;
using Miningcore.Persistence.Repositories;
using Miningcore.Time;
using Miningcore.Util;
using Newtonsoft.Json;
using Contract = Miningcore.Contracts.Contract;
using CNC = Miningcore.Blockchain.Cryptonote.CryptonoteCommands;
using Miningcore.Notifications.Messages;
using System.Globalization;

namespace Miningcore.Blockchain.Cryptonote
{
    [CoinFamily(CoinFamily.Cryptonote)]
    public class CryptonotePayoutHandler : PayoutHandlerBase,
        IPayoutHandler
    {
        public CryptonotePayoutHandler(
            IComponentContext ctx,
            IConnectionFactory cf,
            IMapper mapper,
            IShareRepository shareRepo,
            IBlockRepository blockRepo,
            IBalanceRepository balanceRepo,
            IPaymentRepository paymentRepo,
            IMasterClock clock,
            IMessageBus messageBus) :
            base(cf, mapper, shareRepo, blockRepo, balanceRepo, paymentRepo, clock, messageBus)
        {
            Contract.RequiresNonNull(ctx, nameof(ctx));
            Contract.RequiresNonNull(balanceRepo, nameof(balanceRepo));
            Contract.RequiresNonNull(paymentRepo, nameof(paymentRepo));

            this.ctx = ctx;
        }

        private readonly IComponentContext ctx;
        private DaemonClient daemon;
        private DaemonClient walletDaemon;
        private CryptonoteNetworkType? networkType;
        private CryptonotePoolPaymentProcessingConfigExtra extraConfig;
        private bool walletSupportsTransferSplit;

        protected override string LogCategory => "Cryptonote Payout Handler";

        private async Task<bool> HandleTransferResponseAsync(DaemonResponse<TransferResponse> response, params Balance[] balances)
        {
            var coin = poolConfig.Template.As<CryptonoteCoinTemplate>();

            if (response.Error == null)
            {
                var txHash = response.Response.TxHash;
                var txFee = (decimal) response.Response.Fee / coin.SmallestUnit;

                logger.Info(() => $"[{LogCategory}] Payout transaction id: {txHash}, TxFee was {FormatAmount(txFee)}");

                await PersistPaymentsAsync(balances, txHash);
                NotifyPayoutSuccess(poolConfig.Id, balances, new[] { txHash }, txFee);
                return true;
            }

            else
            {
                logger.Error(() => $"[{LogCategory}] Daemon command '{CryptonoteWalletCommands.Transfer}' returned error: {response.Error.Message} code {response.Error.Code}");

                NotifyPayoutFailure(poolConfig.Id, balances, $"Daemon command '{CryptonoteWalletCommands.Transfer}' returned error: {response.Error.Message} code {response.Error.Code}", null);
                return false;
            }
        }

        private async Task<bool> HandleTransferResponseAsync(DaemonResponse<TransferSplitResponse> response, params Balance[] balances)
        {
            var coin = poolConfig.Template.As<CryptonoteCoinTemplate>();

            if (response.Error == null)
            {
                var txHashes = response.Response.TxHashList;
                var txFees = response.Response.FeeList.Select(x => (decimal) x / coin.SmallestUnit).ToArray();

                logger.Info(() => $"[{LogCategory}] Split-Payout transaction ids: {string.Join(", ", txHashes)}, Corresponding TxFees were {string.Join(", ", txFees.Select(FormatAmount))}");

                await PersistPaymentsAsync(balances, txHashes.First());
                NotifyPayoutSuccess(poolConfig.Id, balances, txHashes, txFees.Sum());
                return true;
            }

            else
            {
                logger.Error(() => $"[{LogCategory}] Daemon command '{CryptonoteWalletCommands.TransferSplit}' returned error: {response.Error.Message} code {response.Error.Code}");

                NotifyPayoutFailure(poolConfig.Id, balances, $"Daemon command '{CryptonoteWalletCommands.TransferSplit}' returned error: {response.Error.Message} code {response.Error.Code}", null);
                return false;
            }
        }

        private async Task<CryptonoteNetworkType> GetNetworkTypeAsync()
        {
            if (!networkType.HasValue)
            {
                var infoResponse = await daemon.ExecuteCmdAnyAsync(logger, CryptonoteCommands.GetInfo, true);
                var info = infoResponse.Response.ToObject<GetInfoResponse>();

                networkType = info.IsTestnet ? CryptonoteNetworkType.Test : CryptonoteNetworkType.Main;
            }

            return networkType.Value;
        }

        private async Task<bool> PayoutBatch(Balance[] balances)
        {
            var coin = poolConfig.Template.As<CryptonoteCoinTemplate>();

            // build request
            var request = new TransferRequest
            {
                Destinations = balances
                    .Where(x => x.Amount > 0)
                    .Select(x =>
                    {
                        ExtractAddressAndPaymentId(x.Address, out var address, out var paymentId);

                        return new TransferDestination
                        {
                            Address = address,
                            Amount = (ulong) Math.Floor(x.Amount * coin.SmallestUnit)
                        };
                    }).ToArray(),

                GetTxKey = true
            };

            if (request.Destinations.Length == 0)
                return true;

            logger.Info(() => $"[{LogCategory}] Paying out {FormatAmount(balances.Sum(x => x.Amount))} to {balances.Length} addresses");

            // send command
            var transferResponse = await walletDaemon.ExecuteCmdSingleAsync<TransferResponse>(logger, CryptonoteWalletCommands.Transfer, request);

            // gracefully handle error -4 (transaction would be too large. try /transfer_split)
            if (transferResponse.Error?.Code == -4)
            {
                if (walletSupportsTransferSplit)
                {
                    logger.Error(() => $"[{LogCategory}] Daemon command '{CryptonoteWalletCommands.Transfer}' returned error: {transferResponse.Error.Message} code {transferResponse.Error.Code}");
                    logger.Info(() => $"[{LogCategory}] Retrying transfer using {CryptonoteWalletCommands.TransferSplit}");

                    var transferSplitResponse = await walletDaemon.ExecuteCmdSingleAsync<TransferSplitResponse>(logger, CryptonoteWalletCommands.TransferSplit, request);

                    return await HandleTransferResponseAsync(transferSplitResponse, balances);
                }
            }

            return await HandleTransferResponseAsync(transferResponse, balances);
        }

        private void ExtractAddressAndPaymentId(string input, out string address, out string paymentId)
        {
            paymentId = null;
            var index = input.IndexOf(PayoutConstants.PayoutInfoSeperator);

            if (index != -1)
            {
                address = input.Substring(0, index);

                if (index + 1 < input.Length)
                {
                    paymentId = input.Substring(index + 1);

                    // ignore invalid payment ids
                    if (paymentId.Length != CryptonoteConstants.PaymentIdHexLength)
                        paymentId = null;
                }
            }

            else
                address = input;
        }

        private async Task PayoutToPaymentId(Balance balance)
        {
            var coin = poolConfig.Template.As<CryptonoteCoinTemplate>();

            ExtractAddressAndPaymentId(balance.Address, out var address, out var paymentId);

            var isIntegratedAddress = string.IsNullOrEmpty(paymentId);

            // build request
            var request = new TransferRequest
            {
                Destinations = new[]
                {
                    new TransferDestination
                    {
                        Address = address,
                        Amount = (ulong) Math.Floor(balance.Amount * coin.SmallestUnit)
                    }
                },
                PaymentId = paymentId,
                GetTxKey = true
            };

            if (!isIntegratedAddress)
                request.PaymentId = paymentId;

            if (!isIntegratedAddress)
                logger.Info(() => $"[{LogCategory}] Paying out {FormatAmount(balance.Amount)} to address {balance.Address} with paymentId {paymentId}");
            else
                logger.Info(() => $"[{LogCategory}] Paying out {FormatAmount(balance.Amount)} to integrated address {balance.Address}");

            // send command
            var result = await walletDaemon.ExecuteCmdSingleAsync<TransferResponse>(logger, CryptonoteWalletCommands.Transfer, request);

            if (walletSupportsTransferSplit)
            {
                // gracefully handle error -4 (transaction would be too large. try /transfer_split)
                if (result.Error?.Code == -4)
                {
                    logger.Info(() => $"[{LogCategory}] Retrying transfer using {CryptonoteWalletCommands.TransferSplit}");

                    result = await walletDaemon.ExecuteCmdSingleAsync<TransferResponse>(logger, CryptonoteWalletCommands.TransferSplit, request);
                }
            }

            await HandleTransferResponseAsync(result, balance);
        }

        #region IPayoutHandler

        public async Task ConfigureAsync(ClusterConfig clusterConfig, PoolConfig poolConfig)
        {
            Contract.RequiresNonNull(poolConfig, nameof(poolConfig));

            this.poolConfig = poolConfig;
            this.clusterConfig = clusterConfig;
            extraConfig = poolConfig.PaymentProcessing.Extra.SafeExtensionDataAs<CryptonotePoolPaymentProcessingConfigExtra>();

            logger = LogUtil.GetPoolScopedLogger(typeof(CryptonotePayoutHandler), poolConfig);

            // configure standard daemon
            var jsonSerializerSettings = ctx.Resolve<JsonSerializerSettings>();

            var daemonEndpoints = poolConfig.Daemons
                .Where(x => string.IsNullOrEmpty(x.Category))
                .Select(x =>
                {
                    if (string.IsNullOrEmpty(x.HttpPath))
                        x.HttpPath = CryptonoteConstants.DaemonRpcLocation;

                    return x;
                })
                .ToArray();

            daemon = new DaemonClient(jsonSerializerSettings, messageBus, clusterConfig.ClusterName ?? poolConfig.PoolName, poolConfig.Id);
            daemon.Configure(daemonEndpoints);

            // configure wallet daemon
            var walletDaemonEndpoints = poolConfig.Daemons
                .Where(x => x.Category?.ToLower() == CryptonoteConstants.WalletDaemonCategory)
                .Select(x =>
                {
                    if (string.IsNullOrEmpty(x.HttpPath))
                        x.HttpPath = CryptonoteConstants.DaemonRpcLocation;

                    return x;
                })
                .ToArray();

            walletDaemon = new DaemonClient(jsonSerializerSettings, messageBus, clusterConfig.ClusterName ?? poolConfig.PoolName, poolConfig.Id);
            walletDaemon.Configure(walletDaemonEndpoints);

            // detect network
            await GetNetworkTypeAsync();

            // detect transfer_split support
            var response = await walletDaemon.ExecuteCmdSingleAsync<TransferResponse>(logger, CryptonoteWalletCommands.TransferSplit);
            walletSupportsTransferSplit = response.Error.Code != CryptonoteConstants.MoneroRpcMethodNotFound;
        }

        public async Task<Block[]> ClassifyBlocksAsync(Block[] blocks)
        {
            Contract.RequiresNonNull(poolConfig, nameof(poolConfig));
            Contract.RequiresNonNull(blocks, nameof(blocks));

            var coin = poolConfig.Template.As<CryptonoteCoinTemplate>();
            var pageSize = 100;
            var pageCount = (int) Math.Ceiling(blocks.Length / (double) pageSize);
            var result = new List<Block>();

            for(var i = 0; i < pageCount; i++)
            {
                // get a page full of blocks
                var page = blocks
                    .Skip(i * pageSize)
                    .Take(pageSize)
                    .ToArray();

                // NOTE: monerod does not support batch-requests
                for(var j = 0; j < page.Length; j++)
                {
                    var block = page[j];

                    var rpcResult = await daemon.ExecuteCmdAnyAsync<GetBlockHeaderResponse>(logger,
                        CryptonoteCommands.GetBlockHeaderByHeight,
                        new GetBlockHeaderByHeightRequest
                        {
                            Height = block.BlockHeight
                        });

                    if (rpcResult.Error != null)
                    {
                        logger.Debug(() => $"[{LogCategory}] Daemon reports error '{rpcResult.Error.Message}' (Code {rpcResult.Error.Code}) for block {block.BlockHeight}");
                        continue;
                    }

                    if (rpcResult.Response?.BlockHeader == null)
                    {
                        logger.Debug(() => $"[{LogCategory}] Daemon returned no header for block {block.BlockHeight}");
                        continue;
                    }

                    var blockHeader = rpcResult.Response.BlockHeader;

                    // update progress
                    block.ConfirmationProgress = Math.Min(1.0d, (double) blockHeader.Depth / CryptonoteConstants.PayoutMinBlockConfirmations);
                    result.Add(block);

                    messageBus.SendMessage(new BlockConfirmationProgressNotification(block.ConfirmationProgress, poolConfig.Id, block.BlockHeight, coin.Symbol));

                    // orphaned?
                    if (blockHeader.IsOrphaned || blockHeader.Hash != block.TransactionConfirmationData)
                    {
                        block.Status = BlockStatus.Orphaned;
                        block.Reward = 0;

                        messageBus.SendMessage(new BlockUnlockedNotification(block.Status, poolConfig.Id,
                            block.BlockHeight, block.Hash, block.Miner, coin.Symbol, null));

                        continue;
                    }

                    // matured and spendable?
                    if (blockHeader.Depth >= CryptonoteConstants.PayoutMinBlockConfirmations)
                    {
                        block.Status = BlockStatus.Confirmed;
                        block.ConfirmationProgress = 1;
                        block.Reward = ((decimal) blockHeader.Reward / coin.SmallestUnit) * coin.BlockrewardMultiplier;

                        logger.Info(() => $"[{LogCategory}] Unlocked block {block.BlockHeight} worth {FormatAmount(block.Reward)}");

                        // build explorer link
                        string explorerLink = null;
                        if (coin.ExplorerBlockLinks.TryGetValue(!string.IsNullOrEmpty(block.Type) ? block.Type : "block", out var blockInfobaseUrl))
                        {
                            if (blockInfobaseUrl.Contains(CoinMetaData.BlockHeightPH))
                                explorerLink = blockInfobaseUrl.Replace(CoinMetaData.BlockHeightPH, block.BlockHeight.ToString(CultureInfo.InvariantCulture));
                            else if (blockInfobaseUrl.Contains(CoinMetaData.BlockHashPH) && !string.IsNullOrEmpty(block.Hash))
                                explorerLink = blockInfobaseUrl.Replace(CoinMetaData.BlockHashPH, block.Hash);
                        }

                        messageBus.SendMessage(new BlockUnlockedNotification(block.Status, poolConfig.Id, 
                            block.BlockHeight, block.Hash, block.Miner, coin.Symbol, explorerLink));
                    }
                }
            }

            return result.ToArray();
        }

        public Task CalculateBlockEffortAsync(Block block, double accumulatedBlockShareDiff)
        {
            block.Effort = accumulatedBlockShareDiff / block.NetworkDifficulty;

            return Task.FromResult(true);
        }

        public override async Task<decimal> UpdateBlockRewardBalancesAsync(IDbConnection con, IDbTransaction tx, Block block, PoolConfig pool)
        {
            var blockRewardRemaining = await base.UpdateBlockRewardBalancesAsync(con, tx, block, pool);

            // Deduct static reserve for tx fees
            blockRewardRemaining -= CryptonoteConstants.StaticTransactionFeeReserve;

            return blockRewardRemaining;
        }

        public async Task PayoutAsync(Balance[] balances)
        {
            Contract.RequiresNonNull(balances, nameof(balances));

            var coin = poolConfig.Template.As<CryptonoteCoinTemplate>();

#if !DEBUG // ensure we have peers
            var infoResponse = await daemon.ExecuteCmdAnyAsync<GetInfoResponse>(logger, CNC.GetInfo);
            if (infoResponse.Error != null || infoResponse.Response == null ||
                infoResponse.Response.IncomingConnectionsCount + infoResponse.Response.OutgoingConnectionsCount < 3)
            {
                logger.Warn(() => $"[{LogCategory}] Payout aborted. Not enough peers (4 required)");
                return;
            }
#endif
            // validate addresses
            balances = balances
                .Where(x =>
                {
                    ExtractAddressAndPaymentId(x.Address, out var address, out var paymentId);

                    var addressPrefix = LibCryptonote.DecodeAddress(address);
                    var addressIntegratedPrefix = LibCryptonote.DecodeIntegratedAddress(address);

                    switch(networkType)
                    {
                        case CryptonoteNetworkType.Main:
                            if (addressPrefix != coin.AddressPrefix &&
                                addressIntegratedPrefix != coin.AddressPrefixIntegrated)
                            {
                                logger.Warn(() => $"[{LogCategory}] Excluding payment to invalid address {x.Address}");
                                return false;
                            }

                            break;

                        case CryptonoteNetworkType.Test:
                            if (addressPrefix != coin.AddressPrefixTestnet &&
                                addressIntegratedPrefix != coin.AddressPrefixIntegratedTestnet)
                            {
                                logger.Warn(() => $"[{LogCategory}] Excluding payment to invalid address {x.Address}");
                                return false;
                            }

                            break;
                    }

                    return true;
                })
                .ToArray();

            // simple balances first
            var simpleBalances = balances
                .Where(x =>
                {
                    ExtractAddressAndPaymentId(x.Address, out var address, out var paymentId);

                    var hasPaymentId = paymentId != null;
                    var isIntegratedAddress = false;
                    var addressIntegratedPrefix = LibCryptonote.DecodeIntegratedAddress(address);

                    switch(networkType)
                    {
                        case CryptonoteNetworkType.Main:
                            if (addressIntegratedPrefix == coin.AddressPrefixIntegrated)
                                isIntegratedAddress = true;
                            break;

                        case CryptonoteNetworkType.Test:
                            if (addressIntegratedPrefix == coin.AddressPrefixIntegratedTestnet)
                                isIntegratedAddress = true;
                            break;
                    }

                    return !hasPaymentId && !isIntegratedAddress;
                })
                .ToArray();

            if (simpleBalances.Length > 0)
#if false
                await PayoutBatch(simpleBalances);
#else
            {
                var maxBatchSize = 28;
                var pageSize = maxBatchSize;
                var pageCount = (int) Math.Ceiling((double) simpleBalances.Length / pageSize);

                for(var i = 0; i < pageCount; i++)
                {
                    var page = simpleBalances
                        .Skip(i * pageSize)
                        .Take(pageSize)
                        .ToArray();

                    if (!await PayoutBatch(page))
                        break;
                }
            }
#endif
            // balances with paymentIds
            var minimumPaymentToPaymentId = extraConfig?.MinimumPaymentToPaymentId ?? poolConfig.PaymentProcessing.MinimumPayment;

            var paymentIdBalances = balances.Except(simpleBalances)
                .Where(x => x.Amount >= minimumPaymentToPaymentId)
                .ToArray();

            foreach(var balance in paymentIdBalances)
                await PayoutToPaymentId(balance);
        }

        #endregion // IPayoutHandler
    }
}
