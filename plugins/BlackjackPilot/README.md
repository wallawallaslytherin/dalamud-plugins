# Blackjack Pilot

Plays your blackjack hand in Aetherphone’s Gamba. Requires Aetherphone 1.3.3.0.

1. Open Gamba, sit at a blackjack table, and place your bet.
2. Open `/blackjackpilot` and click **Turn on**, or use `/blackjackpilot on`.
3. Click **Turn off** or use `/blackjackpilot off` to stop.

Table rules are read automatically. Autoplay starts off and stops if your seat
changes, Gamba disconnects, the rules change, or an action remains unresolved.
Check the table before enabling again. Turning off cannot retract a sent action.

It chooses hit, stand, double, split, or surrender for expected return and declines
insurance. Splitting and doubling can increase the stake on your current hand.
You control seating, new bets, transfers, and cashout.

The strategy uses your hand and the dealer’s visible card, without card counting.
It approximates a finite shoe using independent draws; it cannot guarantee wins.
Use `/blackjackpilot status` to check its state.
