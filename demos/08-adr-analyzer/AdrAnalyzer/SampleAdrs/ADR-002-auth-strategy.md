# ADR-002: Strategia uwierzytelniania i autoryzacji

**Status:** Accepted  
**Data:** 2024-03-20  
**Decydenci:** @security-team, @arch-team  
**Związane ADR:** ADR-001 (Redis dla token storage), ADR-003 (API versioning wpływa na auth endpoints)

## Kontekst

System obsługuje 200,000 aktywnych użytkowników. Poprzednia architektura używała
session cookies przechowywanych w pamięci serwera (nie skaluje się horyzontalnie).

Wymagania security:
- MFA (Multi-Factor Authentication) dla użytkowników enterprise
- Token invalidation musi działać natychmiast (wymóg HR: offboarding < 5 minut)
- Logowanie zdarzeń auth dla SOC/SIEM
- GDPR: prawo do zapomnienia musi objąć tokeny

## Decyzja

**JWT RS256** z:
- Access Token: 15 minut TTL
- Refresh Token: 7 dni TTL, przechowywany jako HttpOnly Secure cookie
- Revocation list w Redis (lista odwołanych tokenów, z TTL = czas życia tokenu)

**Walidacja JWT:**
- KAŻDY serwis musi walidować JWT przez dedykowany `AuthService.ValidateToken(token)` endpoint
- ZABRONIONE: lokalna walidacja JWT (weryfikacja klucza publicznego inline)
- Powód: Centralizacja pozwala na natychmiastowe odwołanie tokenów

**Blacklisting:**
- Odwołany token → jti (JWT ID) trafia do Redis z TTL = expiry tokenu
- AuthService sprawdza blacklistę przy każdej walidacji
- Wymaga Redis z ADR-001

## Konsekwencje

**Pozytywne:**
- Centralna invalidation: logout → token nieważny w < 1 sekundy we wszystkich serwisach
- Stateless serwisy (nie przechowują sesji lokalnie)
- RS256: klucz publiczny bezpiecznie dystrybuowany

**Negatywne:**
- Każde żądanie wymaga call do AuthService → latencja ~5ms dodatkowe
- AuthService staje się single point of failure → wymaga HA (3 instancje minimum)
- Serwisy NIE mogą działać offline bez AuthService

**Niejasności/Ryzyka:**
- ADR-003 definiuje URL-based versioning (`/api/v1/`, `/api/v2/`).
  Czy AuthService endpoint `/auth/validate` podlega wersjonowaniu?
  Jeśli tak: serwisy muszą znać wersję API AuthService → coupling.
  Jeśli nie: jak zarządzamy breaking changes w protokole walidacji?
  **→ To wymaga rozstrzygnięcia (potencjalne ADR-005)**

- Redis z ADR-001 jest oznaczony jako wymagający ADR-004 (cache invalidation).
  Jak token revocation list wchodzi w interakcję z polityką cache invalidation?
  (Tokeny NIE powinny być invalidated przez cache TTL — to osobna logika)
