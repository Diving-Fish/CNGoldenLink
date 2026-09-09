// Deliberately synthetic; these names and tiers are not CNGist catalog assertions.
export function demoSnapshot() {
  return {
    schema:'goldenlink.overlay/1', source:'demo', connected:true,
    catalog:{mapName:'棱光之间', mapNameEn:'Between the Prisms', campaign:'演示地图包', challenge:'FC', tier:'T3', verified:false},
    live:{room:'Room-3', holdingGolden:false, paused:false},
    cct:{roomIndex:3,roomCount:13,checkpointIndex:1,
      checkpoints:[{name:'初光',short:'DAWN',rooms:4},{name:'折射',short:'REFRACT',rooms:5},{name:'余晖',short:'AFTERGLOW',rooms:4}],
      streak:1,bestStreak:4,successRate:51.61,successes:48,attempts:93,entryRate:69.92,
      sessionEntryRate:57.14,goldenDeaths:94,sessionGoldenDeaths:5,
      recent:[true,true,false,true,false,false,true,true,true,false,true,false,true,true,false,true,true,true,false,true]},
    area:{noGoldenBestDeaths:12,totalDeaths:2401}
  };
}
