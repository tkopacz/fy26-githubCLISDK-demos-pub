# ADR-001: Wybór bazy danych dla serwisu zamówień

**Status:** Accepted  
**Data:** 2024-03-15  
**Decydenci:** @arch-team, @backend-lead  
**Związane ADR:** ADR-004 (cache invalidation), ADR-002 (auth storage)

## Kontekst

Serwis zamówień musi obsługiwać 10,000 zamówień/minutę przy peak traffic (Black Friday).
Read:write ratio = 80:20. Zamówienia wymagają ACID transactions.
Dane muszą być dostępne przez 7 lat (wymogi prawne).

Obecny system używa MySQL 5.7 (end-of-life), który wykazuje problemy wydajnościowe
powyżej 5,000 req/min. Plan: migracja przed Q3 2024.

## Rozpatrywane opcje

### Opcja 1: PostgreSQL 16 (standalone)
- Pros: ACID, JSON support, silna community, open source
- Cons: Brak wbudowanego cache, potrzebna oddzielna warstwa dla hot data

### Opcja 2: PostgreSQL 16 + Redis 7 (cache layer)
- Pros: ACID dla transakcji + Redis dla hot data (sessions, catalogues)
- Cons: Dwie technologie, ryzyko inconsistency

### Opcja 3: MongoDB
- Pros: Schema flexibility, horizontal scaling
- Cons: Brak ACID w cross-document transactions (wersja < 4.0), mniej dojrzałe

## Decyzja

Wybieramy **PostgreSQL 16 + Redis 7**.

PostgreSQL dla: zamówień, klientów, płatności (wymagają ACID).
Redis dla: sesje użytkowników, katalog produktów (TTL 10 minut), rate limiting.

## Konsekwencje

**Pozytywne:**
- ACID transactions dla zamówień = spójność danych
- Redis cache = redukcja load na PostgreSQL o ~60% dla read operations
- Dojrzały stack, łatwa rekrutacja

**Negatywne:**
- Dwa systemy do monitorowania i utrzymania
- Ryzyko stale data w Redis (wymagana cache invalidation strategy)
- Dodatkowe koszty infrastruktury (~$300/miesiąc)

**Ryzyka:**
- Cache invalidation strategy MUSI być zdefiniowana przed wdrożeniem Redis (→ ADR-004)
- Bez ADR-004 istnieje ryzyko że użytkownicy zobaczą nieaktualne dane zamówień
