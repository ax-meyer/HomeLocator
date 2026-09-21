<?xml version="1.0" encoding="UTF-8"?>
<!-- AGS-coded address data with official-style names, modeled on real SN, SH and BB WFS responses (2026-09-21). -->
<wfs:FeatureCollection xmlns:wfs="http://www.opengis.net/wfs/2.0"
                       xmlns:gml="http://www.opengis.net/gml/3.2"
                       xmlns:xlink="http://www.w3.org/1999/xlink"
                       xmlns:gn="http://inspire.ec.europa.eu/schemas/gn/4.0"
                       xmlns="http://inspire.ec.europa.eu/schemas/ad/4.0">
  <wfs:member>
    <Address gml:id="Address_SN_1">
      <position><GeographicPosition><geometry>
        <gml:Point gml:id="Address_SN_1_pos" srsName="urn:ogc:def:crs:EPSG::25832"><gml:pos>426500.1 5645500.2</gml:pos></gml:Point>
      </geometry></GeographicPosition></position>
      <locator><AddressLocator><designator><LocatorDesignator>
        <designator>3</designator>
        <type xlink:href="http://inspire.ec.europa.eu/codelist/LocatorDesignatorTypeValue/addressNumber"/>
      </LocatorDesignator></designator></AddressLocator></locator>
      <component xlink:href="#AdminUnitName_sn_land"/>
      <component xlink:href="#AdminUnitName_sn_rb"/>
      <component xlink:href="#AdminUnitName_sn_kreis"/>
      <component xlink:href="#AdminUnitName_sn_gem"/>
      <component xlink:href="#ThoroughfareName_SN_1"/>
      <component xlink:href="#PostalDescriptor_SN_1"/>
    </Address>
  </wfs:member>
  <wfs:member>
    <Address gml:id="Address_SH_1">
      <position><GeographicPosition><geometry>
        <gml:Point gml:id="Address_SH_1_pos" srsName="urn:ogc:def:crs:EPSG::25832"><gml:pos>574500.1 6019500.2</gml:pos></gml:Point>
      </geometry></GeographicPosition></position>
      <locator><AddressLocator><designator><LocatorDesignator>
        <designator>5</designator>
        <type xlink:href="http://inspire.ec.europa.eu/codelist/LocatorDesignatorTypeValue/addressNumber"/>
      </LocatorDesignator></designator></AddressLocator></locator>
      <component xlink:href="#AdminUnitName_sh_land"/>
      <component xlink:href="#AdminUnitName_sh_blank"/>
      <component xlink:href="#AdminUnitName_sh_kreis"/>
      <component xlink:href="#AdminUnitName_sh_gem"/>
      <component xlink:href="#ThoroughfareName_SH_1"/>
    </Address>
  </wfs:member>
  <wfs:member>
    <Address gml:id="Address_BB_1">
      <position><GeographicPosition><geometry>
        <gml:Point gml:id="Address_BB_1_pos" srsName="urn:ogc:def:crs:EPSG::25832"><gml:pos>454500.1 5735500.2</gml:pos></gml:Point>
      </geometry></GeographicPosition></position>
      <locator><AddressLocator><designator><LocatorDesignator>
        <designator>9</designator>
        <type xlink:href="http://inspire.ec.europa.eu/codelist/LocatorDesignatorTypeValue/addressNumber"/>
      </LocatorDesignator></designator></AddressLocator></locator>
      <component xlink:href="#AdminUnitName_bb_land"/>
      <component xlink:href="#AdminUnitName_bb_kreis"/>
      <component xlink:href="#AdminUnitName_bb_gem"/>
      <component xlink:href="#ThoroughfareName_BB_1"/>
    </Address>
  </wfs:member>
  <wfs:additionalObjects>
    <wfs:SimpleFeatureCollection>
      <wfs:member><AdminUnitName gml:id="AdminUnitName_sn_land"><alternativeIdentifier>14</alternativeIdentifier><name><gn:GeographicalName><gn:spelling><gn:SpellingOfName><gn:text>Freistaat Sachsen</gn:text></gn:SpellingOfName></gn:spelling></gn:GeographicalName></name><level xlink:href="http://inspire.ec.europa.eu/codelist/AdministrativeHierarchyLevel/2ndOrder"/></AdminUnitName></wfs:member>
      <wfs:member><AdminUnitName gml:id="AdminUnitName_sn_rb"><alternativeIdentifier>146</alternativeIdentifier><name><gn:GeographicalName><gn:spelling><gn:SpellingOfName><gn:text>NUTS 2-Region Dresden</gn:text></gn:SpellingOfName></gn:spelling></gn:GeographicalName></name><level xlink:href="http://inspire.ec.europa.eu/codelist/AdministrativeHierarchyLevel/3rdOrder"/></AdminUnitName></wfs:member>
      <wfs:member><AdminUnitName gml:id="AdminUnitName_sn_kreis"><alternativeIdentifier>14628</alternativeIdentifier><name><gn:GeographicalName><gn:spelling><gn:SpellingOfName><gn:text>Landkreis Sächsische Schweiz-Osterzgebirge</gn:text></gn:SpellingOfName></gn:spelling></gn:GeographicalName></name><level xlink:href="http://inspire.ec.europa.eu/codelist/AdministrativeHierarchyLevel/4thOrder"/></AdminUnitName></wfs:member>
      <wfs:member><AdminUnitName gml:id="AdminUnitName_sn_gem"><alternativeIdentifier>14628270</alternativeIdentifier><name><gn:GeographicalName><gn:spelling><gn:SpellingOfName><gn:text>Stadt Pirna</gn:text></gn:SpellingOfName></gn:spelling></gn:GeographicalName></name><level xlink:href="http://inspire.ec.europa.eu/codelist/AdministrativeHierarchyLevel/6thOrder"/></AdminUnitName></wfs:member>
      <wfs:member><ThoroughfareName gml:id="ThoroughfareName_SN_1"><name><gn:GeographicalName><gn:spelling><gn:SpellingOfName><gn:text>Remscheider Straße</gn:text></gn:SpellingOfName></gn:spelling></gn:GeographicalName></name></ThoroughfareName></wfs:member>
      <wfs:member><PostalDescriptor gml:id="PostalDescriptor_SN_1"><postName><gn:GeographicalName><gn:spelling><gn:SpellingOfName><gn:text>Pirna</gn:text></gn:SpellingOfName></gn:spelling></gn:GeographicalName></postName><postCode>01796</postCode></PostalDescriptor></wfs:member>
      <wfs:member><AdminUnitName gml:id="AdminUnitName_sh_land"><alternativeIdentifier>01</alternativeIdentifier><name><gn:GeographicalName><gn:spelling><gn:SpellingOfName><gn:text>Schleswig-Holstein</gn:text></gn:SpellingOfName></gn:spelling></gn:GeographicalName></name><level xlink:href="http://inspire.ec.europa.eu/codelist/AdministrativeHierarchyLevel/2ndOrder"/></AdminUnitName></wfs:member>
      <wfs:member><AdminUnitName gml:id="AdminUnitName_sh_blank"><alternativeIdentifier>010</alternativeIdentifier><name><gn:GeographicalName><gn:spelling><gn:SpellingOfName><gn:text>   </gn:text></gn:SpellingOfName></gn:spelling></gn:GeographicalName></name><level xlink:href="http://inspire.ec.europa.eu/codelist/AdministrativeHierarchyLevel/3rdOrder"/></AdminUnitName></wfs:member>
      <wfs:member><AdminUnitName gml:id="AdminUnitName_sh_kreis"><alternativeIdentifier>01002</alternativeIdentifier><name><gn:GeographicalName><gn:spelling><gn:SpellingOfName><gn:text>Kiel, Landeshauptstadt</gn:text></gn:SpellingOfName></gn:spelling></gn:GeographicalName></name><level xlink:href="http://inspire.ec.europa.eu/codelist/AdministrativeHierarchyLevel/4thOrder"/></AdminUnitName></wfs:member>
      <wfs:member><AdminUnitName gml:id="AdminUnitName_sh_gem"><alternativeIdentifier>01002000</alternativeIdentifier><name><gn:GeographicalName><gn:spelling><gn:SpellingOfName><gn:text>Kiel, Landeshauptstadt</gn:text></gn:SpellingOfName></gn:spelling></gn:GeographicalName></name><level xlink:href="http://inspire.ec.europa.eu/codelist/AdministrativeHierarchyLevel/6thOrder"/></AdminUnitName></wfs:member>
      <wfs:member><ThoroughfareName gml:id="ThoroughfareName_SH_1"><name><gn:GeographicalName><gn:spelling><gn:SpellingOfName><gn:text>Schwedenkai</gn:text></gn:SpellingOfName></gn:spelling></gn:GeographicalName></name></ThoroughfareName></wfs:member>
      <wfs:member><AdminUnitName gml:id="AdminUnitName_bb_land"><alternativeIdentifier>12</alternativeIdentifier><name><gn:GeographicalName><gn:spelling><gn:SpellingOfName><gn:text>Brandenburg</gn:text></gn:SpellingOfName></gn:spelling></gn:GeographicalName></name><level xlink:href="http://inspire.ec.europa.eu/codelist/AdministrativeHierarchyLevel/2ndOrder"/></AdminUnitName></wfs:member>
      <wfs:member><AdminUnitName gml:id="AdminUnitName_bb_kreis"><alternativeIdentifier>12052</alternativeIdentifier><name><gn:GeographicalName><gn:spelling><gn:SpellingOfName><gn:text>Cottbus [Chóśebuz]</gn:text></gn:SpellingOfName></gn:spelling></gn:GeographicalName></name><level xlink:href="http://inspire.ec.europa.eu/codelist/AdministrativeHierarchyLevel/4thOrder"/></AdminUnitName></wfs:member>
      <wfs:member><AdminUnitName gml:id="AdminUnitName_bb_gem"><alternativeIdentifier>12052000</alternativeIdentifier><name><gn:GeographicalName><gn:spelling><gn:SpellingOfName><gn:text>Cottbus [Chóśebuz]</gn:text></gn:SpellingOfName></gn:spelling></gn:GeographicalName></name><level xlink:href="http://inspire.ec.europa.eu/codelist/AdministrativeHierarchyLevel/6thOrder"/></AdminUnitName></wfs:member>
      <wfs:member><ThoroughfareName gml:id="ThoroughfareName_BB_1"><name><gn:GeographicalName><gn:spelling><gn:SpellingOfName><gn:text>Karlstraße</gn:text></gn:SpellingOfName></gn:spelling></gn:GeographicalName></name></ThoroughfareName></wfs:member>
    </wfs:SimpleFeatureCollection>
  </wfs:additionalObjects>
</wfs:FeatureCollection>
