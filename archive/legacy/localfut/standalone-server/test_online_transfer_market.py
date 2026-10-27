from __future__ import annotations

import logging
import json
import os
from pathlib import Path
import sys
import tempfile
import threading
import time


def _owned_player(server, *, untradeable: bool = False) -> dict:
    resource_id = next(iter(server.PLAYER_DB))
    item = server.STATE.build_player_item(
        int(resource_id), server.STATE.next_item_id(), "club"
    )
    item["untradeable"] = bool(untradeable)
    server.STATE.save_item(item, insert=True)
    return item


def main() -> int:
    with tempfile.TemporaryDirectory(prefix="project21-online-market-") as temporary:
        root = Path(temporary)
        bootstrap_runtime = root / "bootstrap"
        bootstrap_config = bootstrap_runtime / "config"
        bootstrap_config.mkdir(parents=True)
        os.environ["LOCALFUT_RUNTIME_ROOT"] = str(bootstrap_runtime)
        os.environ["LOCALFUT21_USER_CONFIG_ROOT"] = str(bootstrap_config)
        os.environ["LOCALFUT_MULTI_TENANT_BOOTSTRAP"] = "1"
        os.environ["LOCALFUT21_MARKET_TARGET"] = "0"
        source = Path(__file__).resolve().parent / "runtime-source" / "localfut21"
        sys.path.insert(0, str(source))
        import server  # noqa: PLC0415 - environment must be isolated before import

        registry = server.enable_multi_tenant(root, idle_seconds=60, max_cached_players=20)
        seller_id = "111111111111111111"
        buyer_id = "222222222222222222"
        bidder_id = "333333333333333333"
        rival_id = "444444444444444444"
        market = server._shared_market()

        # A listed card is the seller's real card and is visible to other users.
        with registry.scope(seller_id):
            seller_start = server.STATE.credits()
            sold_item = _owned_player(server)
            listed = market.list_player_item(
                seller_id, sold_item["id"], 500, 1_000, 3600, "Seller"
            )
            trade_id = int(listed["tradeId"])
            assert server.STATE.get_item(sold_item["id"])["pile"] == "trade"
            assert trade_id not in {
                int(row["tradeId"])
                for row in market.search(seller_id, {}, [int(sold_item["resourceId"])])["auctionInfo"]
            }
            try:
                market.buy_now(seller_id, trade_id, 1_000)
                raise AssertionError("seller was allowed to buy their own listing")
            except server.MarketError:
                pass

        with registry.scope(buyer_id):
            buyer_start = server.STATE.credits()
            result = market.search(buyer_id, {}, [])
            assert trade_id in {int(row["tradeId"]) for row in result["auctionInfo"]}
            market.watch(buyer_id, trade_id)
            assert trade_id in {int(row["tradeId"]) for row in market.watchlist(buyer_id)["auctionInfo"]}
            purchased = market.buy_now(buyer_id, trade_id, 1_000)
            assert purchased["pile"] == "unassigned"
            assert server.STATE.credits() == buyer_start - 1_000
            assert server.STATE.get_item(int(purchased["id"])) is not None

        with registry.scope(seller_id):
            assert server.STATE.credits() == seller_start + 1_000
            assert server.STATE.get_item(int(sold_item["id"])) is None
            assert market.tradepile(seller_id)["sold"] == 1
            market.clear_sold(seller_id)
            assert market.tradepile(seller_id)["sold"] == 0

            # Cards that are untradeable or referenced by a squad cannot be listed.
            locked = _owned_player(server, untradeable=True)
            try:
                market.list_player_item(seller_id, locked["id"], 500, 1_000, 3600, "Seller")
                raise AssertionError("untradeable card was listed")
            except server.MarketError:
                pass

            squad_card = _owned_player(server)
            active = server.STATE.get_squad(server.STATE.active_squad_id())
            active["players"] = [{"index": 0, "itemData": {"id": squad_card["id"]}}]
            server.STATE.save_squad(active)
            try:
                market.list_player_item(seller_id, squad_card["id"], 500, 1_000, 3600, "Seller")
                raise AssertionError("squad card was listed")
            except server.MarketError:
                pass

            # Duplicate list submissions return the existing listing.
            bid_item = _owned_player(server)
            first = market.list_player_item(seller_id, bid_item["id"], 500, 2_000, 60, "Seller")
            again = market.list_player_item(seller_id, bid_item["id"], 500, 2_000, 60, "Seller")
            assert first["tradeId"] == again["tradeId"]
            bid_trade_id = int(first["tradeId"])

        # Bids are escrowed, and the previous leader is refunded when outbid.
        with registry.scope(buyer_id):
            before_bid = server.STATE.credits()
            market.place_bid(buyer_id, bid_trade_id, 500)
            assert server.STATE.credits() == before_bid - 500
        with registry.scope(bidder_id):
            challenger_start = server.STATE.credits()
            market.place_bid(bidder_id, bid_trade_id, 600)
            assert server.STATE.credits() == challenger_start - 600
        with registry.scope(buyer_id):
            assert server.STATE.credits() == before_bid

        # Expiry transfers the actual card and pays the seller exactly once.
        market.expire_due(int(time.time()) + 120)
        with registry.scope(bidder_id):
            won = [item for item in server.STATE.list_items("unassigned") if item.get("lastSalePrice") == 600]
            assert len(won) == 1
            after_win = server.STATE.credits()
        try:
            market.buy_now(buyer_id, bid_trade_id, 2_000)
            raise AssertionError("closed auction was purchased again")
        except server.MarketError:
            pass
        with registry.scope(bidder_id):
            assert server.STATE.credits() == after_win
        with registry.scope(seller_id):
            assert server.STATE.get_item(int(bid_item["id"])) is None
            assert server.STATE.credits() == seller_start + 1_600

            orphan_item = _owned_player(server)
            orphan = market.list_player_item(
                seller_id, orphan_item["id"], 500, 2_000, 60, "Seller"
            )
            orphan_trade_id = int(orphan["tradeId"])
        with registry.scope(buyer_id):
            before_orphan_bid = server.STATE.credits()
            market.place_bid(buyer_id, orphan_trade_id, 500)
            assert server.STATE.credits() == before_orphan_bid - 500
        with registry.scope(seller_id):
            # Simulate unexpected club corruption/removal. Settlement must
            # cancel safely instead of trapping every future market request.
            assert server.STATE.delete_item(int(orphan_item["id"]))
        market.expire_due(int(time.time()) + 120)
        with registry.scope(buyer_id):
            assert server.STATE.credits() == before_orphan_bid
            orphan_status = market.status(buyer_id, [orphan_trade_id])["auctionInfo"][0]
            assert orphan_status["tradeState"] == "cancelled"

        with registry.scope(seller_id):
            # Two buyers racing for one card can produce only one winner.
            race_item = _owned_player(server)
            race = market.list_player_item(seller_id, race_item["id"], 500, 1_000, 3600, "Seller")
            race_trade_id = int(race["tradeId"])

        barrier = threading.Barrier(2)
        outcomes: list[tuple[str, str]] = []
        outcome_lock = threading.Lock()

        def buy(identity: str) -> None:
            barrier.wait()
            try:
                market.buy_now(identity, race_trade_id, 1_000)
                outcome = "won"
            except server.MarketError:
                outcome = "lost"
            with outcome_lock:
                outcomes.append((identity, outcome))

        threads = [threading.Thread(target=buy, args=(identity,)) for identity in (buyer_id, rival_id)]
        for thread in threads:
            thread.start()
        for thread in threads:
            thread.join(timeout=10)
            assert not thread.is_alive()
        assert sorted(result for _, result in outcomes) == ["lost", "won"]

        # Exercise the exact HTTP routes used by FIFA 21, including its
        # /trade/<id>/offer Buy Now vocabulary.
        with registry.scope(seller_id):
            route_item = _owned_player(server)
            status, _, body = server.route_fut(
                "POST",
                "/ut/game/fifa21/auctionhouse",
                {},
                json.dumps({
                    "itemData": {"id": route_item["id"]},
                    "startingBid": 500,
                    "buyNowPrice": 1_000,
                    "duration": 3_600,
                }).encode("utf-8"),
            )
            assert status == 200
            route_trade_id = int(json.loads(body)["auctionInfo"][0]["tradeId"])
        with registry.scope(buyer_id):
            status, _, body = server.route_fut(
                "GET", "/ut/game/fifa21/transfermarket?type=player&start=0&num=21", {}, b""
            )
            assert status == 200
            assert route_trade_id in {
                int(row["tradeId"]) for row in json.loads(body)["auctionInfo"]
            }
            status, _, body = server.route_fut(
                "POST",
                f"/ut/game/fifa21/trade/{route_trade_id}/offer",
                {},
                b'{"bid":1000}',
            )
            bought = json.loads(body)
            assert status == 200
            assert bought["auctionInfo"][0]["tradeState"] == "closed"
            assert bought["auctionInfo"][0]["bidState"] == "buyNow"

            # The developer market's AI supply remains available so an empty
            # player market still has usable listings.
            market.ai_target = 5
            ai_search = market.search(buyer_id, {}, server._market_resource_ids())
            ai_listing = next(
                row for row in ai_search["auctionInfo"] if int(row["sellerId"]) == 100_000
            )
            credits_before_ai = server.STATE.credits()
            ai_item = market.buy_now(
                buyer_id, int(ai_listing["tradeId"]), int(ai_listing["buyNowPrice"])
            )
            assert server.STATE.credits() == credits_before_ai - int(ai_listing["buyNowPrice"])
            assert server.STATE.get_item(int(ai_item["id"])) is not None

        # Multiple squads retain separate IDs and only inactive squads may be deleted.
        with registry.scope(buyer_id):
            original_id = server.STATE.active_squad_id()
            created = server.STATE.create_squad({"squadName": "Second XI", "players": []})
            second_id = int(created["id"])
            assert second_id != original_id
            assert len(server.STATE.list_squads()) >= 2
            created["active"] = 1
            server.STATE.save_squad(created)
            assert server.STATE.active_squad_id() == second_id
            assert sum(1 for squad in server.STATE.list_squads() if squad["active"]) == 1
            try:
                server.STATE.delete_squad(second_id)
                raise AssertionError("active squad was deleted")
            except ValueError:
                pass
            server.STATE.delete_squad(original_id)
            assert server.STATE.get_squad(original_id) is None

        market.close()
        registry.close()
        logging.shutdown()
    print("Online transfer market and multiple-squad tests passed.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
