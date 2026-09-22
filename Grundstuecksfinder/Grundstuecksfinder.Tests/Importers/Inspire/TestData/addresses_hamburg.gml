<?xml version="1.0" encoding="UTF-8"?>
<!-- Hamburg-style address data: AdminUnitName uses ad:level hierarchy instead of alternativeIdentifier (AGS). -->
<wfs:FeatureCollection xmlns:wfs="http://www.opengis.net/wfs/2.0"
                        xmlns:gml="http://www.opengis.net/gml/3.2"
                        xmlns:xlink="http://www.w3.org/1999/xlink"
                        xmlns:gn="http://inspire.ec.europa.eu/schemas/gn/4.0"
                        xmlns="http://inspire.ec.europa.eu/schemas/ad/4.0"
                        numberMatched="1" numberReturned="1">
  <wfs:member>
    <Address gml:id="Address_HH_1">
      <position>
        <GeographicPosition>
          <geometry>
            <gml:Point gml:id="Address_HH_1_pos" srsName="urn:ogc:def:crs:EPSG::25832">
              <gml:pos>577813.950 5945208.444</gml:pos>
            </gml:Point>
          </geometry>
        </GeographicPosition>
      </position>
      <locator>
        <AddressLocator>
          <designator>
            <LocatorDesignator>
              <designator>4</designator>
              <type xlink:href="http://inspire.ec.europa.eu/codelist/LocatorDesignatorTypeValue/addressNumber"/>
            </LocatorDesignator>
          </designator>
        </AddressLocator>
      </locator>
      <component xlink:href="#AdminUnitName_country"/>
      <component xlink:href="#AdminUnitName_hamburg"/>
      <component xlink:href="#AdminUnitName_wandsbek"/>
      <component xlink:href="#AdminUnitName_volksdorf"/>
      <component xlink:href="#AdminUnitName_volksdorf_sub"/>
      <component xlink:href="#AdminUnitName_volksdorf_ot"/>
      <component xlink:href="#ThoroughfareName_HH_1"/>
      <component xlink:href="#PostalDescriptor_HH_1"/>
    </Address>
  </wfs:member>
  <wfs:additionalObjects>
    <wfs:SimpleFeatureCollection>
      <wfs:member>
        <AdminUnitName gml:id="AdminUnitName_country">
          <name><gn:GeographicalName><gn:spelling><gn:SpellingOfName><gn:text>Germany</gn:text></gn:SpellingOfName></gn:spelling></gn:GeographicalName></name>
          <level xlink:href="http://inspire.ec.europa.eu/codelist/AdministrativeHierarchyLevel/1stOrder"/>
        </AdminUnitName>
      </wfs:member>
      <wfs:member>
        <AdminUnitName gml:id="AdminUnitName_hamburg">
          <name><gn:GeographicalName><gn:spelling><gn:SpellingOfName><gn:text>Hamburg</gn:text></gn:SpellingOfName></gn:spelling></gn:GeographicalName></name>
          <level xlink:href="http://inspire.ec.europa.eu/codelist/AdministrativeHierarchyLevel/2ndOrder"/>
        </AdminUnitName>
      </wfs:member>
      <wfs:member>
        <AdminUnitName gml:id="AdminUnitName_wandsbek">
          <name><gn:GeographicalName><gn:spelling><gn:SpellingOfName><gn:text>Wandsbek</gn:text></gn:SpellingOfName></gn:spelling></gn:GeographicalName></name>
          <level xlink:href="http://inspire.ec.europa.eu/codelist/AdministrativeHierarchyLevel/3rdOrder"/>
        </AdminUnitName>
      </wfs:member>
      <wfs:member>
        <AdminUnitName gml:id="AdminUnitName_volksdorf">
          <name><gn:GeographicalName><gn:spelling><gn:SpellingOfName><gn:text>Volksdorf</gn:text></gn:SpellingOfName></gn:spelling></gn:GeographicalName></name>
          <level xlink:href="http://inspire.ec.europa.eu/codelist/AdministrativeHierarchyLevel/4thOrder"/>
        </AdminUnitName>
      </wfs:member>
      <wfs:member>
        <AdminUnitName gml:id="AdminUnitName_volksdorf_sub">
          <name><gn:GeographicalName><gn:spelling><gn:SpellingOfName><gn:text>Volksdorf</gn:text></gn:SpellingOfName></gn:spelling></gn:GeographicalName></name>
          <level xlink:href="http://inspire.ec.europa.eu/codelist/AdministrativeHierarchyLevel/5thOrder"/>
        </AdminUnitName>
      </wfs:member>
      <wfs:member>
        <AdminUnitName gml:id="AdminUnitName_volksdorf_ot">
          <name><gn:GeographicalName><gn:spelling><gn:SpellingOfName><gn:text>Volksdorf,OT 0525 (Volksdorf)</gn:text></gn:SpellingOfName></gn:spelling></gn:GeographicalName></name>
          <level xlink:href="http://inspire.ec.europa.eu/codelist/AdministrativeHierarchyLevel/6thOrder"/>
        </AdminUnitName>
      </wfs:member>
      <wfs:member>
        <ThoroughfareName gml:id="ThoroughfareName_HH_1">
          <name><gn:GeographicalName><gn:spelling><gn:SpellingOfName><gn:text>Aalheitengraben</gn:text></gn:SpellingOfName></gn:spelling></gn:GeographicalName></name>
        </ThoroughfareName>
      </wfs:member>
      <wfs:member>
        <PostalDescriptor gml:id="PostalDescriptor_HH_1">
          <postCode>22359</postCode>
        </PostalDescriptor>
      </wfs:member>
    </wfs:SimpleFeatureCollection>
  </wfs:additionalObjects>
</wfs:FeatureCollection>
