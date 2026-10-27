"""Persistent shared FIFA 21 transfer market.

The original LocalFUT market simulated an AI catalogue inside one club database.
This module keeps the proven FIFA 21 response vocabulary, but stores listings in
one process-wide SQLite database and settles player listings against the seller
and buyer Discord-scoped club databases.
"""
from __future__ import annotations

import hashlib
import json
import logging
import random
import sqlite3
import threading
import time
from pathlib import Path
from typing import Any, Callable


log = logging.getLogger("localfut21.market")


def bid_increment(price: int) -> int:
    price = max(0, int(price))
    if price < 1_000:
        return 50
    if price < 10_000:
        return 100
    if price < 50_000:
        return 250
    if price < 100_000:
        return 500
    return 1_000


def price_bounds(resource_id: int, meta: dict[str, Any]) -> tuple[int, int]:
    """Stable listing bounds derived from the developer market implementation."""
    rating = max(0, int(meta.get("rating", 0) or 0))
    rareflag = max(0, int(meta.get("rareflag", 0) or 0))
    special = bool(meta.get("special", False)) or rareflag > 1
    minimum = 10_000 if special else 150
    if rating >= 98:
        maximum = 15_000_000
    elif rating >= 94:
        maximum = 8_000_000
    elif rating >= 90:
        maximum = 3_000_000
    elif rating >= 86:
        maximum = 750_000
    elif rating >= 82:
        maximum = 250_000
    elif rating >= 75:
        maximum = 100_000
    else:
        maximum = 25_000
    if special:
        maximum = max(maximum, 250_000)
    return minimum, maximum


def fair_value(resource_id: int, meta: dict[str, Any]) -> int:
    rating = max(1, int(meta.get("rating", 1) or 1))
    rareflag = max(0, int(meta.get("rareflag", 0) or 0))
    special = bool(meta.get("special", False)) or rareflag > 1
    if rating < 65:
        base = 350 + rating * 8
    elif rating < 75:
        base = 700 + (rating - 65) * 180
    elif rating < 82:
        base = 1_500 + (rating - 75) ** 2 * 450
    elif rating < 87:
        base = 20_000 + (rating - 82) ** 2 * 12_000
    elif rating < 92:
        base = 150_000 + (rating - 87) ** 2 * 90_000
    else:
        base = 900_000 + (rating - 92) ** 2 * 210_000
    if special:
        base = int(base * (1.35 + min(rareflag, 100) / 180.0))
    seed = int.from_bytes(hashlib.sha256(str(int(resource_id)).encode("ascii")).digest()[:4], "big")
    base = int(base * (0.90 + (seed % 21) / 100.0))
    low, high = price_bounds(resource_id, meta)
    return max(low, min(high, base))


class MarketError(ValueError):
    pass


class SharedTransferMarket:
    """One SQLite-backed market shared by every authenticated player."""

    def __init__(
        self,
        path: Path,
        *,
        state_for: Callable[[str], Any],
        player_meta: Callable[[int], dict[str, Any]],
        render_item: Callable[[dict[str, Any]], dict[str, Any]],
        build_preview: Callable[[int, int], dict[str, Any]],
        ai_target: int = 500,
    ) -> None:
        self.path = Path(path)
        self.path.parent.mkdir(parents=True, exist_ok=True)
        self.state_for = state_for
        self.player_meta = player_meta
        self.render_item = render_item
        self.build_preview = build_preview
        self.ai_target = max(0, min(5_000, int(ai_target)))
        self.lock = threading.RLock()
        self.conn = sqlite3.connect(self.path, check_same_thread=False)
        self.conn.row_factory = sqlite3.Row
        with self.lock:
            self.conn.executescript(
                """
                PRAGMA journal_mode=WAL;
                PRAGMA synchronous=FULL;
                PRAGMA busy_timeout=5000;
                CREATE TABLE IF NOT EXISTS listings(
                    trade_id INTEGER PRIMARY KEY AUTOINCREMENT,
                    kind TEXT NOT NULL,
                    seller_id TEXT,
                    seller_name TEXT NOT NULL,
                    seller_item_id INTEGER,
                    resource_id INTEGER NOT NULL,
                    item_json TEXT NOT NULL,
                    starting_bid INTEGER NOT NULL,
                    buy_now INTEGER NOT NULL,
                    current_bid INTEGER NOT NULL DEFAULT 0,
                    bidder_id TEXT,
                    offers INTEGER NOT NULL DEFAULT 0,
                    state TEXT NOT NULL,
                    created_at INTEGER NOT NULL,
                    end_time INTEGER NOT NULL,
                    sold_price INTEGER NOT NULL DEFAULT 0,
                    seller_cleared INTEGER NOT NULL DEFAULT 0
                );
                CREATE INDEX IF NOT EXISTS listings_state_end ON listings(state,end_time);
                CREATE INDEX IF NOT EXISTS listings_seller_state ON listings(seller_id,state);
                CREATE UNIQUE INDEX IF NOT EXISTS listings_live_item
                    ON listings(seller_id,seller_item_id)
                    WHERE kind='player' AND state IN ('active','settling');
                """
            )
            self.conn.commit()

    @staticmethod
    def _identity(value: str) -> str:
        identity = str(value)
        if identity != "single-player" and (
            not identity.isascii() or not identity.isdecimal() or not 5 <= len(identity) <= 32
        ):
            raise MarketError("invalid player identity")
        return identity

    @staticmethod
    def _seller_public_id(identity: str | None) -> int:
        if not identity:
            return 100_000
        return 100_000 + int.from_bytes(hashlib.sha256(identity.encode("ascii")).digest()[:4], "big") % 899_999_999

    def _row(self, trade_id: int) -> sqlite3.Row | None:
        return self.conn.execute("SELECT * FROM listings WHERE trade_id=?", (int(trade_id),)).fetchone()

    def _viewer_bid_state(
        self, row: sqlite3.Row, viewer_id: str, watched_ids: set[int] | None = None
    ) -> str:
        if row["bidder_id"] == viewer_id:
            if (row["state"] == "closed" and int(row["buy_now"] or 0) > 0
                    and int(row["sold_price"] or 0) == int(row["buy_now"] or 0)):
                return "buyNow"
            return "highest" if row["state"] in ("active", "closed") else "none"
        watched = int(row["trade_id"]) in (
            watched_ids if watched_ids is not None else set(self.watch_ids(viewer_id))
        )
        return "outbid" if watched and int(row["current_bid"] or 0) > 0 else "none"

    def _public(
        self, row: sqlite3.Row, viewer_id: str, watched_ids: set[int] | None = None
    ) -> dict[str, Any]:
        raw = json.loads(row["item_json"])
        item = self.render_item(raw)
        resource_id = int(row["resource_id"])
        low, high = price_bounds(resource_id, self.player_meta(resource_id))
        item["marketDataMinPrice"] = low
        item["marketDataMaxPrice"] = high
        if watched_ids is None:
            watched_ids = set(self.watch_ids(viewer_id))
        watched = int(row["trade_id"]) in watched_ids
        now = int(time.time())
        return {
            "tradeId": int(row["trade_id"]),
            "itemData": item,
            "tradeState": str(row["state"]),
            "buyNowPrice": int(row["buy_now"]),
            "currentBid": int(row["current_bid"]),
            "startingBid": int(row["starting_bid"]),
            "offers": int(row["offers"]),
            "watched": watched,
            "bidState": self._viewer_bid_state(row, viewer_id, watched_ids),
            "expires": max(-1 if row["state"] == "closed" else 0, int(row["end_time"]) - now),
            "sellerEstablished": int(row["created_at"]) - 30 * 86400,
            "sellerId": self._seller_public_id(row["seller_id"]),
            "sellerName": str(row["seller_name"]),
            "rating": int(item.get("rating", 0) or 0),
        }

    @staticmethod
    def _currencies(credits: int) -> list[dict[str, Any]]:
        return [{"name": "COINS", "funds": int(credits), "finalFunds": int(credits)}]

    def watch_ids(self, identity: str) -> list[int]:
        state = self.state_for(self._identity(identity))
        raw = state.get("watchlist_trade_ids", [])
        if not isinstance(raw, list):
            return []
        result: list[int] = []
        for value in raw:
            try:
                trade_id = int(value)
            except (TypeError, ValueError):
                continue
            if trade_id > 0 and trade_id not in result:
                result.append(trade_id)
        return result

    def save_watch_ids(self, identity: str, ids: list[int]) -> None:
        clean = list(dict.fromkeys(int(value) for value in ids if int(value) > 0))
        self.state_for(self._identity(identity)).set("watchlist_trade_ids", clean)

    def watch(self, identity: str, trade_id: int) -> None:
        with self.lock:
            if self._row(trade_id) is None:
                return
            ids = self.watch_ids(identity)
            if int(trade_id) not in ids:
                ids.append(int(trade_id))
                self.save_watch_ids(identity, ids)

    def unwatch(self, identity: str, trade_id: int) -> None:
        self.save_watch_ids(identity, [value for value in self.watch_ids(identity) if value != int(trade_id)])

    def _insert_item(self, state: Any, snapshot: dict[str, Any], price: int) -> dict[str, Any]:
        item_id = state.next_item_id()
        item = dict(snapshot)
        item.update({
            "id": item_id,
            "timestamp": int(time.time()),
            "pile": "unassigned",
            "itemState": "free",
            "lastSalePrice": max(0, int(price)),
            "owners": max(1, int(item.get("owners", 0) or 0) + 1),
            "untradeable": False,
            "duplicate": False,
            "isDuplicate": False,
        })
        state.save_item(item, insert=True)
        return item

    def list_player_item(
        self,
        identity: str,
        item_id: int,
        starting_bid: int,
        buy_now: int,
        duration: int,
        seller_name: str,
    ) -> dict[str, Any]:
        identity = self._identity(identity)
        state = self.state_for(identity)
        with self.lock, state.lock:
            owned = state.get_item(item_id)
            if not owned or str(owned.get("itemType", "player")) != "player":
                raise MarketError("player item was not found")
            if bool(owned.get("untradeable")):
                raise MarketError("this card is untradeable")
            if str(owned.get("pile", "")) == "squad":
                raise MarketError("remove this card from your squad first")
            if any(int(item_id) in state._squad_item_ids(squad) for squad in state.list_squads()):
                raise MarketError("remove this card from your squad first")
            resource_id = int(owned.get("resourceId", owned.get("assetId", 0)) or 0)
            meta = self.player_meta(resource_id)
            low, high = price_bounds(resource_id, meta)
            starting_bid = max(low, min(high, int(starting_bid or low)))
            buy_now = max(starting_bid, min(high, int(buy_now or starting_bid)))
            duration = max(60, min(3 * 86400, int(duration or 3600)))
            existing = self.conn.execute(
                "SELECT * FROM listings WHERE seller_id=? AND seller_item_id=? "
                "AND state IN ('active','settling')",
                (identity, int(item_id)),
            ).fetchone()
            if existing is not None:
                return self._public(existing, identity)
            item = dict(owned)
            item.update({"pile": "trade", "itemState": "forSale"})
            state.save_item(item)
            now = int(time.time())
            try:
                cursor = self.conn.execute(
                    "INSERT INTO listings(kind,seller_id,seller_name,seller_item_id,resource_id,item_json,"
                    "starting_bid,buy_now,current_bid,bidder_id,offers,state,created_at,end_time) "
                    "VALUES('player',?,?,?,?,?,?,?,?,NULL,0,'active',?,?)",
                    (identity, str(seller_name or "Project21 Player")[:64], int(item_id), resource_id,
                     json.dumps(item), starting_bid, buy_now, 0, now, now + duration),
                )
                self.conn.commit()
            except Exception:
                item.update({"pile": str(owned.get("pile", "club")), "itemState": str(owned.get("itemState", "free"))})
                state.save_item(item)
                raise
            return self._public(self._row(int(cursor.lastrowid)), identity)

    def seed_ai(self, identity: str, resource_ids: list[int]) -> None:
        if self.ai_target <= 0:
            return
        with self.lock:
            active = int(self.conn.execute(
                "SELECT COUNT(*) FROM listings WHERE kind='ai' AND state='active'"
            ).fetchone()[0])
            missing = min(self.ai_target - active, 250)
            if missing <= 0 or not resource_ids:
                return
            now = int(time.time())
            chosen = random.sample(resource_ids, min(missing, len(resource_ids)))
            for resource_id in chosen:
                meta = self.player_meta(resource_id)
                value = fair_value(resource_id, meta)
                low, high = price_bounds(resource_id, meta)
                starting = max(low, min(high, int(value * random.uniform(0.72, 0.90))))
                buy_now = max(starting, min(high, int(value * random.uniform(1.05, 1.35))))
                preview = self.build_preview(resource_id, 800_000_000 + random.randint(1, 99_999_999))
                self.conn.execute(
                    "INSERT INTO listings(kind,seller_id,seller_name,seller_item_id,resource_id,item_json,"
                    "starting_bid,buy_now,current_bid,bidder_id,offers,state,created_at,end_time) "
                    "VALUES('ai',NULL,?,NULL,?,?,?,?,0,NULL,0,'active',?,?)",
                    (f"FUTUser{random.randint(1000,9999)}", int(resource_id), json.dumps(preview),
                     starting, buy_now, now, now + random.choice((1800, 3600, 7200, 21600))),
                )
            self.conn.commit()

    def _refund(self, identity: str | None, amount: int) -> None:
        if identity and amount > 0:
            self.state_for(identity).add_credits(int(amount))

    def _settle(self, row: sqlite3.Row, buyer_id: str, price: int, *, escrowed: bool) -> dict[str, Any]:
        if row["state"] != "active":
            raise MarketError("trade is no longer active")
        seller_id = row["seller_id"]
        if seller_id and seller_id == buyer_id:
            raise MarketError("you cannot buy your own listing")
        buyer = self.state_for(buyer_id)
        price = max(0, int(price))
        if not escrowed:
            if buyer.credits() < price:
                raise MarketError("not enough coins")
            buyer.add_credits(-price)
        snapshot = json.loads(row["item_json"])
        new_item: dict[str, Any] | None = None
        seller = self.state_for(seller_id) if seller_id else None
        seller_paid = False
        seller_item_deleted = False
        try:
            new_item = self._insert_item(buyer, snapshot, price)
            if seller is not None:
                seller.add_credits(price)
                seller_paid = True
                seller_item_deleted = seller.delete_item(int(row["seller_item_id"] or 0))
                if not seller_item_deleted:
                    raise MarketError("seller card is no longer available")
            cursor = self.conn.execute(
                "UPDATE listings SET state='closed',current_bid=?,bidder_id=?,sold_price=?,end_time=? "
                "WHERE trade_id=? AND state='active'",
                (price, buyer_id, price, int(time.time()), int(row["trade_id"])),
            )
            if cursor.rowcount != 1:
                raise MarketError("trade is no longer active")
            self.conn.commit()
        except Exception:
            self.conn.rollback()
            if seller is not None and seller_item_deleted:
                seller.save_item(snapshot, insert=True)
            if seller is not None and seller_paid:
                seller.add_credits(-price)
            if new_item is not None:
                buyer.delete_item(int(new_item["id"]))
            if not escrowed:
                buyer.add_credits(price)
            raise
        self.watch(buyer_id, int(row["trade_id"]))
        return new_item

    def buy_now(self, identity: str, trade_id: int, offered: int = 0) -> dict[str, Any]:
        identity = self._identity(identity)
        with self.lock:
            self.expire_due()
            row = self._row(trade_id)
            if row is None:
                raise MarketError("invalid trade ID")
            if row["state"] != "active":
                raise MarketError("trade is no longer active")
            price = int(row["buy_now"])
            if price <= 0 or (offered > 0 and int(offered) < price):
                raise MarketError("offer is below Buy Now")
            previous_bidder = row["bidder_id"]
            previous_bid = int(row["current_bid"] or 0)
            escrowed = previous_bidder == identity and previous_bid > 0
            if escrowed:
                delta = price - previous_bid
                buyer = self.state_for(identity)
                if buyer.credits() < delta:
                    raise MarketError("not enough coins")
                buyer.add_credits(-delta)
                try:
                    return self._settle(row, identity, price, escrowed=True)
                except Exception:
                    buyer.add_credits(delta)
                    raise
            elif previous_bidder:
                self._refund(previous_bidder, previous_bid)
                try:
                    return self._settle(row, identity, price, escrowed=False)
                except Exception:
                    self.state_for(previous_bidder).add_credits(-previous_bid)
                    raise
            return self._settle(row, identity, price, escrowed=False)

    def place_bid(self, identity: str, trade_id: int, amount: int) -> dict[str, Any]:
        identity = self._identity(identity)
        amount = int(amount)
        with self.lock:
            self.expire_due()
            row = self._row(trade_id)
            if row is None or row["state"] != "active":
                raise MarketError("trade is no longer active")
            if row["seller_id"] == identity:
                raise MarketError("you cannot bid on your own listing")
            buy_now = int(row["buy_now"])
            if buy_now > 0 and amount >= buy_now:
                return self.buy_now(identity, trade_id, buy_now)
            current = int(row["current_bid"] or 0)
            minimum = int(row["starting_bid"]) if current <= 0 else current + bid_increment(current)
            if amount < minimum:
                raise MarketError("bid is below the minimum")
            buyer = self.state_for(identity)
            previous_bidder = row["bidder_id"]
            if previous_bidder == identity:
                delta = amount - current
                if delta <= 0:
                    return self._public(row, identity)
                if buyer.credits() < delta:
                    raise MarketError("not enough coins")
                buyer.add_credits(-delta)
            else:
                if buyer.credits() < amount:
                    raise MarketError("not enough coins")
                buyer.add_credits(-amount)
                self._refund(previous_bidder, current)
            self.conn.execute(
                "UPDATE listings SET current_bid=?,bidder_id=?,offers=offers+1 WHERE trade_id=?",
                (amount, identity, int(trade_id)),
            )
            self.conn.commit()
            self.watch(identity, trade_id)
            return self._public(self._row(trade_id), identity)

    def expire_due(self, now: int | None = None) -> None:
        now = int(time.time() if now is None else now)
        with self.lock:
            rows = self.conn.execute(
                "SELECT * FROM listings WHERE state='active' AND end_time<=?", (now,)
            ).fetchall()
            for row in rows:
                bidder = row["bidder_id"]
                if bidder and int(row["current_bid"] or 0) > 0:
                    try:
                        self._settle(
                            row, bidder, int(row["current_bid"]), escrowed=True
                        )
                    except MarketError as error:
                        self._refund(bidder, int(row["current_bid"] or 0))
                        self.conn.execute(
                            "UPDATE listings SET state='cancelled',current_bid=0,"
                            "bidder_id=NULL,sold_price=0,end_time=? WHERE trade_id=?",
                            (now, int(row["trade_id"])),
                        )
                        self.conn.commit()
                        log.error(
                            "Cancelled unresolvable expired trade %s: %s",
                            int(row["trade_id"]), error,
                        )
                    continue
                if row["kind"] == "player" and row["seller_id"]:
                    seller = self.state_for(row["seller_id"])
                    item = seller.get_item(int(row["seller_item_id"] or 0))
                    if item:
                        item = dict(item)
                        item.update({"pile": "trade", "itemState": "free"})
                        seller.save_item(item)
                self.conn.execute(
                    "UPDATE listings SET state='expired',end_time=? WHERE trade_id=?",
                    (now, int(row["trade_id"])),
                )
            self.conn.commit()

    @staticmethod
    def _qint(query: dict[str, list[str]], *keys: str, default: int = 0) -> int:
        for key in keys:
            values = query.get(key)
            if values:
                try:
                    return int(values[0])
                except (TypeError, ValueError):
                    pass
        return default

    @staticmethod
    def _qstr(query: dict[str, list[str]], *keys: str, default: str = "") -> str:
        for key in keys:
            values = query.get(key)
            if values:
                return str(values[0])
        return default

    def _matches(self, row: sqlite3.Row, query: dict[str, list[str]]) -> bool:
        item = json.loads(row["item_json"])
        wanted = self._qint(query, "maskedDefId", "defId", "defid", "assetId", "assetid", "resourceId")
        if wanted and wanted not in (int(item.get("assetId", 0) or 0), int(row["resource_id"])):
            return False
        for keys, field in ((('nation','nat','nationId','nationid'),'nation'),
                            (('league','leag','leagueId','leagueid'),'leagueId'),
                            (('club','team','clubId','clubid','teamId','teamid'),'teamid')):
            value = self._qint(query, *keys)
            if value and int(item.get(field, 0) or 0) != value:
                return False
        position = self._qstr(query, "pos", "position", "preferredPosition").upper()
        if position and str(item.get("preferredPosition", "")).upper() != position:
            return False
        rating = int(item.get("rating", 0) or 0)
        level = self._qstr(query, "lev", "level").lower()
        if level == "bronze" and rating > 64:
            return False
        if level == "silver" and not 65 <= rating <= 74:
            return False
        if level == "gold" and rating < 75:
            return False
        rare = self._qstr(query, "rare", "rarity").lower()
        special = int(item.get("rareflag", 0) or 0) > 1 or bool(item.get("special"))
        if rare in ("true", "1", "special") and not special:
            return False
        min_buy = self._qint(query, "minb", "minBuy", "minBuyNowPrice")
        max_buy = self._qint(query, "maxb", "maxBuy", "maxBuyNowPrice")
        if min_buy and int(row["buy_now"]) < min_buy:
            return False
        if max_buy and int(row["buy_now"]) > max_buy:
            return False
        return True

    def search(self, identity: str, query: dict[str, list[str]], ai_resource_ids: list[int]) -> dict[str, Any]:
        identity = self._identity(identity)
        self.expire_due()
        self.seed_ai(identity, ai_resource_ids)
        start = max(0, self._qint(query, "start", "offset"))
        count = max(1, min(50, self._qint(query, "num", "count", default=16)))
        with self.lock:
            rows = [row for row in self.conn.execute(
                "SELECT * FROM listings WHERE state='active' ORDER BY trade_id"
            ).fetchall() if row["seller_id"] != identity and self._matches(row, query)]
            rows.sort(key=lambda row: (-int(json.loads(row["item_json"]).get("rating", 0) or 0), int(row["trade_id"])))
            watched_ids = set(self.watch_ids(identity))
            page = [
                self._public(row, identity, watched_ids)
                for row in rows[start:start + count]
            ]
        credits = self.state_for(identity).credits()
        return {"auctionInfo": page, "credits": credits, "total": len(rows), "currencies": self._currencies(credits)}

    def tradepile(self, identity: str) -> dict[str, Any]:
        identity = self._identity(identity)
        self.expire_due()
        with self.lock:
            rows = self.conn.execute(
                "SELECT * FROM listings WHERE kind='player' AND seller_id=? AND seller_cleared=0 ORDER BY trade_id",
                (identity,),
            ).fetchall()
            auctions = [self._public(row, identity) for row in rows]
        selling = sum(1 for row in rows if row["state"] == "active")
        sold = sum(1 for row in rows if row["state"] == "closed")
        credits = self.state_for(identity).credits()
        return {"auctionInfo": auctions, "credits": credits, "total": len(rows), "count": len(rows),
                "maxAuctionsAllowed": 100, "offered": 0, "selling": selling, "sold": sold,
                "currencies": self._currencies(credits)}

    def watchlist(self, identity: str, query: dict[str, list[str]] | None = None) -> dict[str, Any]:
        identity = self._identity(identity)
        self.expire_due()
        query = query or {}
        ids = self.watch_ids(identity)
        with self.lock:
            rows = [self._row(trade_id) for trade_id in ids]
            rows = [row for row in rows if row is not None]
            live_ids = [int(row["trade_id"]) for row in rows]
            if live_ids != ids:
                self.save_watch_ids(identity, live_ids)
            auctions = [self._public(row, identity) for row in rows]
        offset = max(0, self._qint(query, "offset"))
        count = max(1, min(100, self._qint(query, "count", default=50)))
        credits = self.state_for(identity).credits()
        winning = sum(1 for row in rows if row["bidder_id"] == identity and row["state"] == "active")
        outbid = sum(1 for row in rows if row["bidder_id"] not in (None, identity) and row["state"] == "active")
        return {"auctionInfo": auctions[offset:offset + count], "credits": credits, "total": len(rows),
                "count": len(rows), "offset": offset, "winning": winning, "outbid": outbid,
                "currencies": self._currencies(credits)}

    def status(self, identity: str, trade_ids: list[int]) -> dict[str, Any]:
        identity = self._identity(identity)
        self.expire_due()
        with self.lock:
            rows = [self._row(value) for value in trade_ids]
            auctions = [self._public(row, identity) for row in rows if row is not None]
        credits = self.state_for(identity).credits()
        return {"auctionInfo": auctions, "credits": credits, "currencies": self._currencies(credits)}

    def clear_trade(self, identity: str, trade_id: int) -> dict[str, Any]:
        identity = self._identity(identity)
        with self.lock:
            row = self._row(trade_id)
            if row is None or row["seller_id"] != identity:
                return self.tradepile(identity)
            if row["state"] == "active" and row["bidder_id"]:
                raise MarketError("an auction with bids cannot be cancelled")
            if row["state"] in ("active", "expired"):
                state = self.state_for(identity)
                item = state.get_item(int(row["seller_item_id"] or 0))
                if item:
                    item = dict(item)
                    item.update({"pile": "club", "itemState": "free"})
                    state.save_item(item)
            self.conn.execute("UPDATE listings SET seller_cleared=1 WHERE trade_id=?", (int(trade_id),))
            self.conn.commit()
        return self.tradepile(identity)

    def clear_sold(self, identity: str) -> dict[str, Any]:
        identity = self._identity(identity)
        with self.lock:
            self.conn.execute(
                "UPDATE listings SET seller_cleared=1 WHERE seller_id=? AND state='closed'",
                (identity,),
            )
            self.conn.commit()
        return self.tradepile(identity)

    def active_count(self) -> int:
        self.expire_due()
        with self.lock:
            return int(self.conn.execute("SELECT COUNT(*) FROM listings WHERE state='active'").fetchone()[0])

    def close(self) -> None:
        with self.lock:
            self.conn.close()
