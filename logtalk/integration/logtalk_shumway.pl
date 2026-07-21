%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
%
%  Integration file for Shumway (.NET Prolog)  -- experimental bring-up
%  Modeled on integration/logtalk_gp.pl (GNU Prolog).
%
%  Usage (from a shell, with the Shumway REPL on PATH as `shumway`):
%
%      set LOGTALKHOME=<your Logtalk installation>
%      set LOGTALKUSER=<your Logtalk installation>
%      shumway integration/logtalk_shumway.pl
%
%  or consult the three files directly, in order:
%
%      shumway adapters/shumway.pl paths/paths.pl core/core.pl
%
%  This file is part of Logtalk <https://logtalk.org/>
%  SPDX-License-Identifier: Apache-2.0
%
%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%


% load the Logtalk core files, resolving the installation directory from the
% LOGTALKHOME environment variable via getenv/2 (Shumway's consult/1 does not
% expand $VARs in paths).

:- initialization((
	getenv('LOGTALKHOME', Home),
	atom_concat(Home, '/adapters/shumway.pl', Adapter),
	atom_concat(Home, '/paths/paths.pl', Paths),
	atom_concat(Home, '/core/core.pl', Core),
	consult(Adapter),
	consult(Paths),
	consult(Core)
)).
