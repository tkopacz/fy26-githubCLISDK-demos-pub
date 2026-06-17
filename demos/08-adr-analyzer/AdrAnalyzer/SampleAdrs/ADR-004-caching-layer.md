# ADR-004: Strategia cache invalidation dla Redis

**Status:** Proposed (nierozwiązane)  
**Data:** 2024-04-15  
**Decydenci:** @arch-team (w toku)  
**Związane ADR:** ADR-001 (Redis wymagany), ADR-002 (token revocation w Redis)

## Kontekst

ADR-001 wybrał Redis jako warstwę cache dla:
1. Sesje użytkowników
2. Katalog produktów (TTL 10 minut)
3. Rate limiting

ADR-002 dodał nowe użycie Redis:
4. Token revocation list (blacklist JWT)

Problem: Polityki cache invalidation dla tych czterech przypadków użycia są **sprzeczne**:

### Przypadek 1: Katalog produktów
- Aktualizacja katalogu → natychmiastowe unieważnienie cache (write-through)
- Problem: przy 15 mikroserwisach, który serwis odpowiada za invalidację?

### Przypadek 2: Sesje
- Wylogowanie → natychmiastowe unieważnienie
- TTL: 30 minut sliding window czy 8h max?
- Czy sesja w Redis to główna sesja czy cache sesji z PostgreSQL?

### Przypadek 3: Rate limiting counters
- Te NIE powinny być invalidowane — to cel, nie cache
- Ale TTL musi być zarządzane ostrożnie

### Przypadek 4: Token revocation (z ADR-002)
- Odwołany token → MUSI pozostać w Redis do czasu jego naturalnego wygaśnięcia
- NIE może być usunięty przez ogólną cache invalidation policy
- **Krytyczny konflikt**: jeśli zastosujemy flush cache = nowe dane, zniszczymy blacklistę tokenów!

## Rozpatrywane strategie invalidacji

### Strategia A: TTL-only (prostota)
- Każdy klucz ma TTL, automatyczna invalidacja
- Problem: katalogu nie odświeżamy na czas; blacklista bezpieczna ale sesje wygasają nieoczekiwanie

### Strategia B: Event-driven invalidation (pub/sub)
- Zapis do PostgreSQL → event do Redis Pub/Sub → invalidacja konkretnych kluczy
- Problem: 15 serwisów musi subskrybować eventy, kto publikuje?
- Problem: ADR nie definiuje event bus — dodatkowa zależność (Kafka? Redis Streams?)

### Strategia C: Separate namespaces + different policies
- `cache:catalog:*` — TTL 10min, write-through
- `sessions:*` — TTL 30min sliding, explicit delete on logout
- `ratelimit:*` — własny TTL, brak invalidacji
- `revoked_tokens:*` — TTL = expiry tokenu, NIGDY nie flush całości
- Problem: skomplikowana konfiguracja, potrzeba fence dla cache flush operations

## Decyzja

**BRAK DECYZJI — w trakcie dyskusji.**

Otwarte kwestie blokujące:
1. Kto jest właścicielem cache invalidation per namespace?
2. Czy potrzebujemy event bus dla invalidacji (jeśli tak → nowe ADR)?
3. Jak zapewnić że operacja "flush all cache" NIE dotknie `revoked_tokens:*`?
4. Jak monitorować hit rate per namespace (alerting)?

## Konsekwencje braku decyzji

**BLOKUJE ADR-001 wdrożenie Redis na produkcję.**

Bez tej decyzji:
- Ryzyko: użytkownicy widzą nieaktualne ceny/stany magazynowe
- Ryzyko KRYTYCZNY: odwołanie tokenu JWT może być ignorowane jeśli ktoś zflushuje cache
  (naruszenie wymogu offboarding < 5 minut z ADR-002)
- Ryzyko: sesje nie wygasają poprawnie

**Data wymagana decyzja:** przed wdrożeniem Redis (planowane Q3 2024 wg ADR-001)

**Właściciel blokady:** @arch-team + @security-team muszą uzgodnić punkt 3 i 4.

## Ryzyka związane z ADR-002

Z ADR-002: "Tokeny NIE powinny być invalidated przez cache TTL — to osobna logika."
To potwierdza że musimy mieć separację namespace i ochronę przed flush operations.
Potencjalna implikacja: `FLUSHDB` jest ZABRONIONE na produkcji bez audytu security-team.
